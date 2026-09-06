# PassCrate.WebSite

A .NET 10 ASP.NET Core MVC website with a React island and Tailwind CSS, built with Vite. MVC owns routing and renders the home and legal pages. React enhances the light/dark preview gallery. The home page has a static image fallback; all legal content is readable without JavaScript.

## Run

Requirements: the repository's .NET SDK 10.0.400, Node.js 22.12+ and npm. MAUI workloads are not needed to build this project individually.

From the repository root:

```powershell
dotnet run --project src/PassCrate.WebSite --launch-profile http
```

Open http://localhost:5210. The first build installs locked npm dependencies; normal builds regenerate frontend assets. Routes: `/` and `/privacy` (with `#privacy` and `#terms` anchors). `/Home/Privacy` remains available as an alias.

For CSS/React editing, keep this running in a second terminal and refresh the browser after changes:

```powershell
cd src/PassCrate.WebSite/ClientApp
npm run watch
```

Razor edits require restarting the app or using `dotnet watch --project src/PassCrate.WebSite`.

## Publish

```powershell
dotnet publish src/PassCrate.WebSite -c Release -o artifacts/website-publish
```

Publishing builds and includes `wwwroot/dist/site.js` and `site.css`. Node is a build dependency only; hosting requires the ASP.NET Core 10 runtime. After changing package.json, run `npm install` in ClientApp and commit package-lock.json. CI should run `npm ci` in ClientApp before publishing to verify/reinstall the exact frontend dependency graph. `-p:SkipClientBuild=true` is only for builds where frontend assets have already been generated.

Production uses HTTPS redirection and HSTS. Configure HTTPS and any trusted reverse proxy forwarding for the target host before deployment.

## Shared legal content

`HomeController.Privacy` supplies `LegalDocuments.Privacy` and `LegalDocuments.Terms` from PassCrate.Core. The Razor view renders every introduction, section, version and effective date directly, with normal Razor HTML encoding. Edit the shared Core documents to update the app and website together.

## Design assets

The background and palette follow https://passcrate.com/: near-black, violet and magenta glows, and lavender/pink headings. Assets are served locally; there are no external fonts, CDN scripts, tracking, or forms.

`wwwroot/images/app-design-preview.png` is copied from the repository's `artifacts/passcrate-group-ui-redesign-concept.png`. It is a design concept, not a verified application screenshot, and the page labels it accordingly. Light and dark crops use CSS to present each half of this original image. Replace this asset and the gallery crop markup/styles with approved device screenshots when available. The app is marked Coming soon because the repository states that it has not been deployed; no store links are invented.
