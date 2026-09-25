# Grundstücksfinder

Grundstücksfinder is a self-hosted search tool for German real-estate parcels. It imports
the official cadastral open data (ALKIS) the German states publish, stores it in Postgres, and
lets you search parcels by postal code, municipality, and area (m²) through a Blazor Server
web UI — with geocoded map markers via OpenStreetMap/Nominatim.

Currently ships with sources for **13 of the 16 Bundesländer** (~17.8 million addresses, see
[Data sources](#data-sources)): North Rhine-Westphalia from its Grundsteuer bulk file, all
others from their cadastral web services — parcels and addresses joined by location, or parcels
that name their own addresses (configured under `Import:Inspire:Sources`, see
[States from cadastral web services](#states-from-cadastral-web-services)). The import pipeline
is built around a pluggable `IPropertySource` interface, so adding another Bundesland (or
country) is a matter of writing one source class — no changes to the shared import, scheduling,
storage, or search code required.

![Grundstücksfinder screenshot](grundstuecksfinder.png)

## Features

- Search parcels by PLZ, Gemeinde, and min/max area
- Map view with geocoded markers (Nominatim, cached to respect its usage policy)
- Automatic imports of new open-data releases, planned per source (see [Import schedule](#import-schedule))
- Multiple data sources can coexist without one import wiping another's rows
- Import history (`ImportRuns`) and per-source state (`SourceStates`) in the database; failed
  imports and known holes show up on `/health`

## Data sources

Every source is official open data that also allows commercial use; the exact credits each
licence asks for are on the app's Impressum page. Row counts and import times are from full
local imports in September 2026.

| State | Parcels (area) | Addresses | Licence | Rows | Import |
|---|---|---|---|---:|---:|
| Baden-Württemberg | INSPIRE WFS | Hauskoordinaten file | dl-de/by-2.0 | 3.09 M | 2 h 30 min |
| Berlin | ALKIS WFS | address register WFS | dl-de/zero-2.0 | 0.40 M | 30 min |
| Brandenburg | INSPIRE WFS | INSPIRE WFS | dl-de/by-2.0 | 0.87 M | 1 h |
| Bremen | ALKIS-vereinfacht WFS | ALKIS Gebäudeadressen WFS | CC BY 4.0 | 0.18 M | 6 min |
| Hamburg | INSPIRE WFS | INSPIRE WFS (startIndex) | dl-de/by-2.0 | 0.30 M | 10 min |
| Hessen | INSPIRE WFS | Hauskoordinaten file | dl-de/zero-2.0 | 1.63 M | 2 h 25 min |
| Niedersachsen | INSPIRE WFS | INSPIRE WFS | CC BY 4.0 | 2.64 M | 2 h 15 min |
| Nordrhein-Westfalen | Grundsteuer bulk file | (same file) | dl-de/zero-2.0 | 3.95 M | 5 min |
| Rheinland-Pfalz | ALKIS-vereinfacht WFS | the parcels' Lagebezeichnung | dl-de/by-2.0 | 1.73 M | 1 h 40 min |
| Saarland | INSPIRE WFS | Hauskoordinaten OGC API | dl-de/by-2.0, CC BY 4.0 | 0.34 M | 8 min |
| Sachsen | INSPIRE WFS | INSPIRE WFS | dl-de/by-2.0 | 0.99 M | 50 min |
| Schleswig-Holstein | INSPIRE WFS | INSPIRE WFS | CC BY 4.0 | 0.95 M | 50 min |
| Thüringen | ALKIS-vereinfacht WFS | the parcels' Lagebezeichnung | CC BY 4.0 | 0.69 M | 3 h 45 min |

Where a state publishes no PLZ (BB, BW, HE, NI, RP, TH), it is filled from the OpenStreetMap
postcode areas ([yetzt/postleitzahlen](https://github.com/yetzt/postleitzahlen), ODbL 1.0).

Not covered, for licence reasons:

- **Sachsen-Anhalt** — its ALKIS "OpenData" WFS would work like Rheinland-Pfalz's, but the
  licence statements contradict each other (dl-de/by-2.0 in the service's metadata, dl-de/by-1.0
  and the state's fee regulation elsewhere). Waiting for LVermGeo to confirm dl-de/by-2.0.
- **Mecklenburg-Vorpommern** — parcels are free, but the address service may not be built into
  paid websites or apps without a licence from LAiV M-V.
- **Bayern** — no free vector parcels or addresses at all (open data has only a raster parcel
  map); statewide ALKIS data costs about €56,000.

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
   service. On first startup it imports every enabled source, one after another (about 16 hours
   for all 13 states); after that the import check runs nightly at 03:00 UTC.

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

The first start then imports all sources back to back, which takes about 16 hours; the site
shows no data until the first source completes.

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
- A source with an **approximate** fingerprint (WFS hit counts, which drift daily in active
  states — every state but NRW, since its parcels always come from a WFS) is re-imported when
  it changed and the data is at least `MinAgeDays` old, or in any case once the data is
  `MaxAgeDays` old.
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

### States from cadastral web services

These states publish no single file carrying both an address and a parcel's official area.
Mostly each import joins two datasets: parcels (official area + outline) from the state's
INSPIRE `cp:CadastralParcel` WFS (or another parcel feature type, see below), fetched tile by
tile, and addresses from whichever of the state's address datasets is usable and fastest, each
address matched to the parcel containing it. `AddressSource.Type` picks it:

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

A `FlatWfs` address type maps the elements of a non-INSPIRE address feature in `Fields`
(`Street`, `HouseNumber`, `HouseNumberSuffix`, `Plz`, `Ort`, `Gemeinde`); a city state whose
data names no Gemeinde gets `FixedGemeinde` instead, with its Ortsteil as the Ort:

```json
{
  "Source": "be",
  "ParcelWfsUrl": "https://gdi.berlin.de/services/wfs/alkis_flurstuecke",
  "ParcelFeatureType": { "TypeName": "alkis_flurstuecke:flurstuecke", "AreaField": "afl" },
  "AddressSource": {
    "Type": "FlatWfs",
    "Url": "https://gdi.berlin.de/services/wfs/adressen_berlin",
    "TypeName": "adressen_berlin:adressen_berlin",
    "Fields": {
      "Street": "str_name", "HouseNumber": "hnr", "HouseNumberSuffix": "hnr_zusatz",
      "Plz": "plz", "Ort": "ort_name", "FixedGemeinde": "Berlin"
    }
  },
  "Crs": "urn:ogc:def:crs:EPSG::25833",
  "BoundingBox": { "MinX": 365000, "MinY": 5795000, "MaxX": 420000, "MaxY": 5840000 }
}
```

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

Tests marked `Category=Live` query every state's real services with tiny requests, to notice
when one changes its format; they need network access and fail when a service is down. To skip
them:

```bash
dotnet test --project Grundstuecksfinder.Tests --filter-not-trait "Category=Live"
```

## License

Licensed under the [GNU Affero General Public License v3.0](LICENSE) (AGPL-3.0-or-later).
