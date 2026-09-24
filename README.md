# Grundstücksfinder

Grundstücksfinder is a self-hosted search tool for German real-estate parcels. It imports
official open data ("Grundsteuer") published by German states, stores it in
Postgres, and lets you search parcels by postal code, municipality, and area (m²) through a
Blazor Server web UI — with geocoded map markers via OpenStreetMap/Nominatim.

Currently ships with sources for **North Rhine-Westphalia (NRW)** and the states that publish
their cadastre as separate INSPIRE parcel and address services (configured under
`Import:Inspire:Sources`). The import pipeline is built around a pluggable `IPropertySource`
interface, so adding another Bundesland (or country) is a matter of writing one source class —
no changes to the shared import, scheduling, storage, or search code required.

![Grundstücksfinder screenshot](grundstuecksfinder.png)

## Features

- Search parcels by PLZ, Gemeinde, and min/max area
- Map view with geocoded markers (Nominatim, cached to respect its usage policy)
- Automatic imports of new open-data releases, planned per source (see [Import schedule](#import-schedule))
- Multiple data sources can coexist without one import wiping another's rows
- Import history (`ImportRuns`) and per-source state (`SourceStates`) in the database; failed
  imports and known holes show up on `/health`

## Quick setup

### Run it (Docker Compose)

Requires Docker and a `web` external network for Traefik (or drop the Traefik labels/network
in `docker-compose.yml` if you don't use it).

1. Copy the env template and fill in real values:

   ```bash
   cp .env.example .env
   ```

   | Variable | Meaning |
   |---|---|
   | `DB_PASSWORD` | Password for the `homelocator` Postgres user, shared by `app` and `db` |
   | `DOMAIN` | Public hostname Traefik should route to this app |
   | `GITHUB_REPOSITORY_OWNER` | GitHub user/org owning the `ghcr.io/<owner>/grundstuecksfinder` image |

2. Start it:

   ```bash
   docker compose up -d
   ```

   The app listens on port `8080` behind Traefik and connects to the bundled `postgres:17-alpine`
   service. On first startup it imports every enabled source, one after another (this takes
   hours for the large states); after that the import check runs nightly at 03:00 UTC.

### Local development

`docker-compose.dev.yml` only starts a local Postgres instance (hardcoded dev password, no
`.env` needed):

```bash
docker compose -f docker-compose.dev.yml up -d
cd Grundstuecksfinder
dotnet run --project Grundstuecksfinder
```

Config for local dev lives in `Grundstuecksfinder/appsettings.Development.json`
(`ConnectionStrings:DefaultConnection`, `Import:Nrw:*`). To skip waiting on the real NRW
download while developing, set `EnableDevSeeding` to `true` (in `launchSettings.json` or as an
environment variable) — this seeds a handful of fake parcels from `part.csv` (or a single
hardcoded fallback row) instead of hitting the network.

### Import schedule

Every run (at startup and nightly at 03:00 UTC) probes each enabled source cheaply and then
decides what to import:

- A source without imported data is imported right away; after the database is dropped, all
  sources are imported back to back in the first run.
- A source with an **exact** fingerprint (the publisher's own version marker, e.g. NRW's manifest
  timestamp) is re-imported as soon as it changes, and an unchanged file is never downloaded
  again.
- A source with an **approximate** fingerprint (INSPIRE WFS hit counts, which drift daily in
  active states) is re-imported when it changed and the data is at least `MinAgeDays` old, or
  in any case once the data is `MaxAgeDays` old.
- Of those re-imports, at most `MaxRoutineImportsPerRun` run per night, most overdue first, so
  the multi-hour states are spread over several nights. A failed import is simply due again in
  the next run; the previous data stays in place meanwhile.

```json
"Import": {
  "Refresh": { "MinAgeDays": 30, "MaxAgeDays": 90, "MaxRoutineImportsPerRun": 1 },
  "Inspire": { "Sources": [ { "Source": "hh", "Refresh": { "MaxAgeDays": 30 }, ... } ] }
}
```

The per-source `Refresh` section is optional and overrides the defaults for that source only.
Broken values fail startup.

### Adding another region's source

Implement `IPropertySource` (see `Services/Importers/Nrw/NrwPropertyImporter.cs` for a
CSV-based example) and register it in `Program.cs`:

```csharp
builder.Services.AddScoped<IPropertySource, YourRegionPropertySource>();
```

The shared `ImportRunner`/`RefreshPlanner`/`PropertyBulkWriter` handle scheduling, the import
history, per-source row replacement and the Postgres bulk write — your source only needs to
know how to probe its upstream version and fetch its region's data.

## Running tests

Integration tests spin up Postgres via Testcontainers, so Docker must be running:

```bash
dotnet test --project Grundstuecksfinder.Tests
```

## License

Licensed under the [GNU Affero General Public License v3.0](LICENSE) (AGPL-3.0-or-later).
