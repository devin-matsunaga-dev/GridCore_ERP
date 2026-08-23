# Keycloak realm import

`gridcore-realm.json` is the GridCore realm as code: the eight roles from `GridCoreRoles`, the
`gridcore-api` audience client, the `gridcore-web` SPA client, and one test user per role.

**Development only.** The realm carries checked-in test users with a well-known password
(`Dev!Passw0rd`), so `InfrastructureComposition` imports it only when the AppHost runs in the
Development environment. A production or public demo realm — same roles and clients, real users,
no default passwords — is WP-5.1's to supply.

Keycloak imports a realm once, into an empty database. The Keycloak container keeps a data volume,
so edits to this file are picked up only after the volume is dropped:

```bash
docker volume ls --filter name=keycloak     # find the volume
docker volume rm <name>                     # then `aspire run` re-imports
```
