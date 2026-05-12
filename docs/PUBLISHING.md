# Publishing GetSet to NuGet.org

Follow these steps once to put the package live so anyone can `dotnet add package GetSet`.

---

## Step 1 — Create a NuGet.org account

1. Go to https://www.nuget.org/users/account/LogOn
2. Sign up (or log in with your Microsoft account).
3. Verify your email address.

---

## Step 2 — Get your API key

1. Log in → click your username (top right) → **API Keys**.
2. Click **Create**.
3. Give it a name (e.g. "GetSet publish key").
4. Under **Glob Pattern**, enter `GetSet*`.
5. Set expiry (365 days is fine).
6. Click **Create** and **copy the key** — you won't see it again.

---

## Step 3 — Update package metadata

Open `src/GetSet/GetSet.csproj` and fill in your real values:

```xml
<Authors>Your Real Name</Authors>
<PackageProjectUrl>https://github.com/YOUR_USERNAME/GetSet</PackageProjectUrl>
<RepositoryUrl>https://github.com/YOUR_USERNAME/GetSet</RepositoryUrl>
```

Bump `<Version>` whenever you publish an update, following semver:
- Bug fix → `1.0.1`
- New feature → `1.1.0`
- Breaking change → `2.0.0`

---

## Step 4 — Build and pack

```bash
# From the repo root:
dotnet build src/GetSet/GetSet.csproj -c Release
dotnet pack  src/GetSet/GetSet.csproj -c Release -o ./nupkg
```

This creates `./nupkg/GetSet.1.0.0.nupkg`.

---

## Step 5 — Push to NuGet.org

```bash
dotnet nuget push ./nupkg/GetSet.1.0.0.nupkg \
  --api-key YOUR_API_KEY_HERE \
  --source https://api.nuget.org/v3/index.json
```

It takes 5–15 minutes for the package to become searchable on nuget.org.

---

## Step 6 — Verify

```bash
dotnet add package GetSet
```

Or search for it at https://www.nuget.org/packages/GetSet

---

## Setting up GitHub Actions (optional, recommended)

Create `.github/workflows/publish.yml` in your repo:

```yaml
name: Publish to NuGet

on:
  push:
    tags:
      - 'v*'   # triggers on git tag like v1.0.0

jobs:
  publish:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4

      - name: Setup .NET
        uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '8.x'

      - name: Build
        run: dotnet build src/GetSet/GetSet.csproj -c Release

      - name: Pack
        run: dotnet pack src/GetSet/GetSet.csproj -c Release -o ./nupkg

      - name: Push
        run: |
          dotnet nuget push ./nupkg/*.nupkg \
            --api-key ${{ secrets.NUGET_API_KEY }} \
            --source https://api.nuget.org/v3/index.json
```

Then in your GitHub repo → **Settings → Secrets → Actions**, add:
- Name: `NUGET_API_KEY`
- Value: your NuGet API key

Now every time you `git tag v1.2.3 && git push --tags`, CI packs and publishes automatically.

---

## Checklist before first publish

- [ ] `<Authors>` has your real name
- [ ] `<PackageProjectUrl>` points to your GitHub repo
- [ ] `<RepositoryUrl>` points to your GitHub repo  
- [ ] `README.md` is at repo root (it's included as `<PackageReadmeFile>`)
- [ ] Tests pass: `dotnet test`
- [ ] Package ID `GetSet` is not taken on nuget.org (check first!)
  - If taken, change `<PackageId>` to e.g. `GetSet.Generator` or `YourName.GetSet`
