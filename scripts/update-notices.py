#!/usr/bin/env python3
"""
Writes THIRD-PARTY-NOTICES.md: the notices the licenses of what the Winche Server image ships ask for.

  - Deno, installed in the image (its version from the Dockerfile; its license in scripts/licenses/deno-LICENSE.md).
  - The NuGet packages the server publishes (from a fresh `dotnet publish`'s deps.json; Winche's own left out), with
    each package's copyright and its license: the package's own license file when it has one, else the standard
    text of its license expression (scripts/licenses/<SPDX id>.txt), plus any NOTICE file it carries.
  - The npm packages in the console web app's build (its production dependencies), with their own license files.

Run it after changing dependencies or the Deno version, then commit what it changed:

  scripts/update-notices.py [path to winche-console]   (default: ../winche-console next to this repository)
"""
import json
import os
import re
import subprocess
import sys
import tempfile
from collections import OrderedDict
from html import unescape

HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
LICENSES = os.path.join(HERE, "scripts", "licenses")
WEB = os.path.abspath(sys.argv[1] if len(sys.argv) > 1 else os.path.join(HERE, "..", "winche-console"))
NUGET = os.environ.get("NUGET_PACKAGES") or os.path.expanduser("~/.nuget/packages")
DOTNET = os.environ.get("DOTNET") or (os.path.expanduser("~/.dotnet/dotnet") if os.path.exists(os.path.expanduser("~/.dotnet/dotnet")) else "dotnet")


def read(path):
    with open(path, encoding="utf-8", errors="replace") as f:
        return f.read().replace("\r\n", "\n").strip()


def spdx_text(expression):
    """The standard text(s) of a license expression, e.g. "MIT" or "MIT OR CC0-1.0" (the first we have)."""
    ids = [i for i in re.split(r"[\s()]+|\bAND\b|\bOR\b", expression) if i and i not in ("AND", "OR", "WITH")]
    texts = [read(os.path.join(LICENSES, f"{i}.txt")) for i in ids if os.path.exists(os.path.join(LICENSES, f"{i}.txt"))]
    if not texts:
        sys.exit(f"No license text for {expression!r}: add scripts/licenses/<id>.txt (https://github.com/spdx/license-list-data).")
    # "A OR B": one is enough; "A AND B": both apply.
    return texts[0] if " OR " in expression else "\n\n---\n\n".join(texts)


def license_files(folder):
    return sorted(f for f in os.listdir(folder) if re.match(r"(?i)^(licen[cs]e|copying)(\.(md|txt))?$", f) and os.path.isfile(os.path.join(folder, f)))


def notice_files(folder):
    return sorted(f for f in os.listdir(folder) if re.match(r"(?i)^(notice|third-?party-?notices?)(\.(md|txt))?$", f) and os.path.isfile(os.path.join(folder, f)))


def deno():
    version = re.search(r"ARG DENO_VERSION=(\S+)", read(os.path.join(HERE, "Dockerfile"))).group(1)
    return version, read(os.path.join(LICENSES, "deno-LICENSE.md"))


def nuget_packages():
    """The third-party packages a release publish ships, with their copyright and license text."""
    with tempfile.TemporaryDirectory() as out:
        subprocess.run([DOTNET, "publish", os.path.join(HERE, "src", "Winche.Server", "Winche.Server.csproj"), "-c", "Release", "-o", out, "-v", "q"],
                       check=True, stdout=subprocess.DEVNULL)
        deps = json.load(open(os.path.join(out, "Winche.Server.deps.json")))
    found = []
    for key, lib in sorted(deps["libraries"].items(), key=lambda kv: kv[0].lower()):
        if lib["type"] != "package" or key.startswith("Winche."):
            continue
        name, version = key.split("/")
        folder = os.path.join(NUGET, name.lower(), version)
        spec = read(os.path.join(folder, next(f for f in os.listdir(folder) if f.endswith(".nuspec"))))
        tag = lambda t: (m.group(1).strip() if (m := re.search(rf"<{t}>([^<]*)</{t}>", spec)) else None)
        lic = re.search(r'<license type="(\w+)"[^>]*>([^<]+)</license>', spec)
        if lic and lic.group(1) == "file":
            expression, text = "see the license", read(os.path.join(folder, lic.group(2).strip()))
        elif lic:
            expression = lic.group(2).strip()
            own = license_files(folder)
            text = read(os.path.join(folder, own[0])) if own else spdx_text(expression)
        else:
            sys.exit(f"{key}: no license in its nuspec (only {tag('licenseUrl')}): add its text by hand.")
        notices = [read(os.path.join(folder, f)) for f in notice_files(folder)]
        found.append(dict(name=name, version=version, license=expression, copyright=unescape(tag("copyright") or tag("authors") or ""),
                          url=tag("projectUrl"), text=text, notices=notices))
    return found


def npm_packages():
    """The console web app's production dependencies (what its build can contain), with their license files."""
    out = subprocess.run(["npm", "ls", "--omit=dev", "--all", "--parseable"], cwd=WEB, check=True, capture_output=True, text=True).stdout
    seen, found = set(), []
    for folder in out.split("\n")[1:]:
        if not folder.strip():
            continue
        pkg = json.load(open(os.path.join(folder, "package.json")))
        key = (pkg["name"], pkg["version"])
        if key in seen:
            continue
        seen.add(key)
        expression = pkg.get("license") or " OR ".join(l.get("type", "") for l in pkg.get("licenses", [])) or "UNKNOWN"
        own = license_files(folder)
        author = pkg.get("author")
        author = author.get("name") if isinstance(author, dict) else author
        text = read(os.path.join(folder, own[0])) if own else spdx_text(expression)
        if not own and author:
            text = f"Copyright (c) {author}\n\n{text}"
        repo = pkg.get("repository")
        repo = repo.get("url") if isinstance(repo, dict) else repo
        found.append(dict(name=pkg["name"], version=pkg["version"], license=expression, url=pkg.get("homepage") or repo, text=text,
                          notices=[read(os.path.join(folder, f)) for f in notice_files(folder)]))
    return sorted(found, key=lambda p: p["name"].lower())


def grouped(packages):
    """Packages that carry the very same license text, together (the text once)."""
    groups = OrderedDict()
    for p in packages:
        groups.setdefault(p["text"], []).append(p)
    return groups


def fence(text):
    return "```text\n" + text + "\n```"


def main():
    version, deno_license = deno()
    nuget = nuget_packages()
    npm = npm_packages()
    lines = [
        "# Third-party notices",
        "",
        "The Winche Server image includes software made by others, under their own licenses. This file carries the",
        "notices those licenses ask for. It is made by `scripts/update-notices.py`: don't edit it by hand.",
        "",
        "- [Deno](#deno)",
        f"- [The server's .NET packages](#the-servers-net-packages) ({len(nuget)})",
        f"- [The console's web packages](#the-consoles-web-packages) ({len(npm)})",
        "",
        "## Deno",
        "",
        f"The image includes the `deno` program, version {version} (https://deno.com, https://github.com/denoland/deno),",
        "which runs Winche Functions. It is used unchanged, as released by the Deno authors.",
        "",
        fence(deno_license),
        "",
        "## The server's .NET packages",
        "",
        "NuGet packages the server is built with, under these licenses. Each package's copyright notice is listed with it.",
        "",
    ]
    for text, packages in grouped(nuget).items():
        lines.append("### " + ", ".join(sorted({p["license"] for p in packages})))
        lines.append("")
        for p in packages:
            lines.append(f"- {p['name']} {p['version']}" + (f" — {p['copyright']}" if p["copyright"] else "") + (f" — {p['url']}" if p["url"] else ""))
        lines += ["", fence(text), ""]
        for p in packages:
            for n in p["notices"]:
                lines += [f"Notice from {p['name']}:", "", fence(n), ""]
    lines += [
        "## The console's web packages",
        "",
        "npm packages the console's web app is built with, each with its own license (packages with the same text share it).",
        "",
    ]
    for text, packages in grouped(npm).items():
        lines.append("### " + ", ".join(f"{p['name']} {p['version']}" for p in packages))
        lines.append("")
        lines.append("License: " + ", ".join(sorted({p["license"] for p in packages})) + "." +
                     (" " + ", ".join(sorted({p["url"] for p in packages if p["url"]})) if any(p["url"] for p in packages) else ""))
        lines += ["", fence(text), ""]
        for p in packages:
            for n in p["notices"]:
                lines += [f"Notice from {p['name']}:", "", fence(n), ""]
    with open(os.path.join(HERE, "THIRD-PARTY-NOTICES.md"), "w", encoding="utf-8") as f:
        f.write("\n".join(lines).rstrip() + "\n")
    print(f"THIRD-PARTY-NOTICES.md: Deno {version}, {len(nuget)} .NET packages, {len(npm)} web packages.")


if __name__ == "__main__":
    main()
