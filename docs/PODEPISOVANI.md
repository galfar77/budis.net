# Podepsání aplikací (macOS a Windows)

Dokud aplikace nejsou podepsané, systémy při prvním spuštění varují: macOS „nelze ověřit vývojáře“, Windows SmartScreen „Chráněno systémem Windows“.
GitHub Actions v tomto repozitáři umí aplikace podepsat samo, ale potřebuje k tomu **certifikáty, které si musíte pořídit vy** (ověřují vaši totožnost).
Dokud tyto hodnoty v repozitáři nejsou, sestavení běží jako dřív, jen bez podpisu. Nic se nerozbije.

Hodnoty se zadávají v GitHubu: repozitář → Settings → Secrets and variables → Actions → New repository secret.

## macOS: podpis a notarizace

Potřebujete placený účet **Apple Developer Program** (99 USD ročně).

1. V účtu vytvořte certifikát **Developer ID Application** (Certificates, IDs & Profiles) a stáhněte ho do Klíčenky.
2. V Klíčence ho exportujte i s privátním klíčem jako `.p12` (zvolte heslo) a převeďte na jeden řádek: `base64 -i certifikat.p12 | pbcopy`.
3. Na https://account.apple.com vytvořte **heslo pro aplikaci** (App-Specific Password).
4. V GitHubu přidejte tajné hodnoty:

| Název | Hodnota |
| --- | --- |
| `MACOS_CERTIFICATE` | text z kroku 2 (base64 z `.p12`) |
| `MACOS_CERTIFICATE_PASSWORD` | heslo k `.p12` |
| `APPLE_ID` | vaše Apple ID (e-mail) |
| `APPLE_TEAM_ID` | desetimístné Team ID z účtu Apple Developer |
| `APPLE_APP_PASSWORD` | heslo pro aplikaci z kroku 3 |

Další sestavení na větvi `main` pak aplikaci podepíše (hardened runtime), odešle k notarizaci, počká na výsledek a přibalí razítko.
Totéž jde i na vlastním Macu: `SIGN_IDENTITY="Developer ID Application: Jméno (TEAMID)" NOTARY_APPLE_ID=… NOTARY_TEAM_ID=… NOTARY_PASSWORD=… ./scripts/make-app.sh`.

## Windows: podpis programu

Potřebujete certifikát pro podepisování kódu od certifikační autority (běžně 100 až 400 USD ročně; EV certifikát hned získá důvěru SmartScreen, u běžného se důvěra buduje časem).

1. Z certifikátu s privátním klíčem udělejte soubor `.pfx` (heslo si zvolte) a převeďte ho na jeden řádek: `[Convert]::ToBase64String([IO.File]::ReadAllBytes("certifikat.pfx"))`.
2. V GitHubu přidejte tajné hodnoty:

| Název | Hodnota |
| --- | --- |
| `WINDOWS_CERTIFICATE` | text z kroku 1 (base64 z `.pfx`) |
| `WINDOWS_CERTIFICATE_PASSWORD` | heslo k `.pfx` |

Další sestavení na větvi `main` podepíše oba programy (`x64` i `arm64`) pomocí `signtool` s časovým razítkem a podpis ověří.
Pozor: nové verze aplikace si stahují aktualizace z Releases, takže podepsaný program se po aktualizaci vymění za podepsaný.

## Co jsem neověřil

Kroky podepisování jsem napsal, ale nemohl jsem je spustit, protože nemám vaše certifikáty. Při prvním sestavení s vyplněnými hodnotami
sledujte log kroků „Certifikát Developer ID…“, „Notarizace“ a „Podepsání programů“; případnou chybu pak opravíme.
