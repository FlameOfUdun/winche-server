# Samples

Services for working on Winche Server locally, and a demo app.

- `docker-compose.yml`: PostgreSQL (`localhost:5433`, user and password `winche`), MinIO (`localhost:9100`, console on
  `9101`) and Keycloak (`localhost:8080`, realm `winche` imported from `keycloak/winche-realm.json`).

  ```sh
  docker compose -f samples/docker-compose.yml up -d
  ```

- Keycloak as the console's company sign-in: issuer `http://host.docker.internal:8080/realms/winche`, client
  `winche-console` (public; it allows `http://localhost:5080/_console/*` as redirects: for a server on another port,
  add its `…/_console/*` in Keycloak's admin console, `admin` / `admin` on the master realm). A server in Docker reaches
  Keycloak at that address; for your browser to as well, add `127.0.0.1 host.docker.internal` to `/etc/hosts`.
  Its users include `admin` / `admin` (realm role `Admin`) and `viewer` / `viewer`.

- `flutter-demo/`: a Flutter app using the Dart SDKs against a local server (documents, expanded links to files and
  users).
