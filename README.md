# Grundstücksfinder

Grundstücksfinder is a self-hosted search tool for German real-estate parcels. It imports
official open data ("Grundsteuer") published by German states, stores it in
Postgres, and lets you search parcels by postal code, municipality, and area (m²) through a
Blazor Server web UI — with geocoded map markers via OpenStreetMap/Nominatim.

Currently ships with sources for **North Rhine-Westphalia (NRW)** and the states that publish
parcels and addresses separately — parcels as an INSPIRE service, addresses as an INSPIRE
service or a statewide file — joined by location (configured under `Import:Inspire:Sources`,
see [INSPIRE states](#inspire-states)). The import pipeline is built around a pluggable `IPropertySource`
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

### Upgrading from a version with `ImportLogs`

The import history was redesigned and all database migrations were squashed into one fresh
`InitialCreate`. On a database created by an earlier version it fails at startup with
`relation "..." already exists`. **Drop the database before deploying** this version, e.g.:

```bash
docker compose stop app
docker compose exec db dropdb -U homelocator homelocator
docker compose exec db createdb -U homelocator homelocator
docker compose up -d app
```

The first start then imports all sources back to back, which takes hours; the site shows no
data until the first source completes.

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
  sources are imported back to back in the first run. Sources whose previous attempt failed (or
  was cut short by a crash) go after the others.
- A source with an **exact** fingerprint (the publisher's own version marker, e.g. NRW's manifest
  timestamp) is re-imported as soon as it changes, and an unchanged file is never downloaded
  again.
- A source with an **approximate** fingerprint (INSPIRE WFS hit counts, which drift daily in
  active states — every INSPIRE state, since its parcels always come from a WFS) is re-imported when it changed and the data is at least `MinAgeDays` old, or
  in any case once the data is `MaxAgeDays` old.
- Such re-imports only happen in the nightly run, at most `MaxRoutineImportsPerRun` of them,
  most overdue first, so the multi-hour states are spread over several nights; the run at
  startup only does first imports. A failed import is simply due again in the next run; the
  previous data stays in place meanwhile. Ages count from when an import started.
- Only one instance imports at a time (a database-wide lock); another one skips its run.

```json
"Import": {
  "Refresh": { "MinAgeDays": 30, "MaxAgeDays": 90, "MaxRoutineImportsPerRun": 1 },
  "Inspire": { "Sources": [ { "Source": "hh", "Refresh": { "MaxAgeDays": 30 }, ... } ] }
}
```

The per-source `Refresh` section is optional and overrides the defaults for that source only
(ages up to 3650 days). Broken values fail startup.

Downloads (NRW's ~1 GB ZIP and similar files) go to `Import:WorkDirectory`, by default a
`grundstuecksfinder` directory under the system temp directory, and are deleted once read.

### INSPIRE states

These states publish no dataset carrying both an address and a parcel's official area, so each
import joins two: parcels (official area + outline) from the state's INSPIRE `cp:CadastralParcel`
WFS (or another parcel feature type, see below), fetched tile by tile, and addresses from whichever of the state's address datasets is usable
and fastest, each address matched to the parcel containing it. `AddressSource.Type` picks it:

| Type | Addresses from | Used by |
|---|---|---|
| `InspireWfs` | the INSPIRE `ad:Address` WFS, per tile alongside the parcels | SH, SN, BB, NI |
| `InspireWfsStartIndex` | the same, paged once by `startIndex` (its bbox filter is broken) | HH |
| `OgcApiFeatures` | an OGC API Features collection with the ALKIS Hauskoordinaten schema | SL |
| `HkFile` | the statewide "Hauskoordinaten" text file (ZSHH format) in a ZIP | BW, HE |
| `FlatWfs` | a WFS with flat address features (`TypeName`, `Fields` map the elements) | HB, BE |
| `ParcelLagebezeichnung` | no join: the parcels' own Lagebezeichnung text (see below) | RP, TH |

```json
{
  "Source": "bw",
  "ParcelWfsUrl": "https://owsproxy.lgl-bw.de/owsproxy/wfs/WFS_INSP_BW_Flst_ALKIS",
  "AddressSource": {
    "Type": "HkFile",
    "Url": "https://opengeodata.lgl-bw.de/data/hk/hk_bw.zip",
    "Locator": "StaticUrl",
    "Member": "adressen-bw.txt",
    "AllowedQualities": [ "A", "B" ]
  },
  "Crs": "urn:ogc:def:crs:EPSG::25832",
  "BoundingBox": { "MinX": 370000, "MinY": 5250000, "MaxX": 610000, "MaxY": 5520000 },
  "FillMissingPlzFromPostcodeAreas": true
}
```

A Hauskoordinaten file is found by its `Locator` — `StaticUrl` (a fixed URL, versioned by its
ETag/Last-Modified) or `HessenDownloadCenter` (the REST listing of a folder in Hessen's download
center, whose download links only work on the day they are listed) — so the address part of the
fingerprint is the publisher's own version. The parcel part is still a WFS hit count, though, so
the state as a whole stays **approximate**: its file is downloaded again whenever the state is
re-imported under its `MinAgeDays`/`MaxAgeDays`, even if the file itself hasn't changed. It goes
to `Import:WorkDirectory` like every download, is read from the ZIP entry named by `Member` (a
name, or a pattern with `*`/`?`), and deleted right after.
Only the qualities in `AllowedQualities` (default A and B) are imported: BW's quality C rows
carry house numbers made up from their coordinates. An Ortsteil that only numbers a district
(HE's "Frankfurt Bezirk 32") is ignored in favour of the Gemeinde. Sources without a PLZ get the
one of the OpenStreetMap postcode area around them (`FillMissingPlzFromPostcodeAreas`).

#### Parcels that name their own addresses

Where a state's INSPIRE service is missing or worse, `ParcelFeatureType` points the parcel side
at another feature type of its parcel WFS — typically the AdV "ALKIS vereinfacht" `ave:Flurstueck`,
whose parcels carry their official area (`flaeche`), their Gemeinde, and their Lagebezeichnungen
as text (`lagebeztxt`: "Löwenhofstraße 5; Vordere Synagogenstraße 2, 2 A"). With
`AddressSource.Type` `ParcelLagebezeichnung` there is then nothing to join: every address a
parcel names becomes a row with that parcel's area — an address two parcels both name becomes
two rows. Completeness is checked in parcels, the PLZ comes from the postcode area around the
parcel, and `Namespace` binds the type's prefix for servers that need a WFS `NAMESPACES`
parameter (as does `AddressSource.Namespace` for a `FlatWfs` address type).

```json
{
  "Source": "rp",
  "ParcelWfsUrl": "https://geo5.service24.rlp.de/wfs/alkis_rp.fcgi",
  "ParcelFeatureType": {
    "TypeName": "ave:Flurstueck",
    "AreaField": "flaeche",
    "GemeindeField": "gemeinde",
    "LagebezeichnungField": "lagebeztxt"
  },
  "AddressSource": { "Type": "ParcelLagebezeichnung" },
  "Crs": "urn:ogc:def:crs:EPSG::25832",
  "BoundingBox": { "MinX": 280000, "MinY": 5410000, "MaxX": 480000, "MaxY": 5660000 },
  "FillMissingPlzFromPostcodeAreas": true
}
```

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
