# tgrep-gui

GUI desktop Windows per [Microsoft tgrep](https://github.com/microsoft/tgrep), scritta in **C# / .NET 10, WinUI 3, Windows App SDK 2.4.0 stabile**. Riprende il flusso cartella → file → righe di [rg-gui](https://github.com/kcowolf/rg-gui), con un’interfaccia Fluent indipendente. Nessuna dipendenza WPF, WinForms, UWP, Electron o webview.

## Prerequisiti

- Windows 11 consigliato; minimo Windows 10 build 17763. Mica viene attivato dove supportato.
- **.NET 10 SDK** x64, oppure ARM64 per compilare su ARM. `dotnet --list-sdks` deve elencare un SDK 10.0, non soltanto il runtime.
- Visual Studio 2026 con sviluppo WinUI/.NET desktop per l’esperienza F5. La compilazione tramite CLI funziona senza Visual Studio: il Windows SDK BuildTools viene ripristinato da NuGet.
- Windows App SDK **2.4.0 stabile**, CommunityToolkit.Mvvm **8.4.2**, CommunityToolkit.WinUI.Controls.Sizers **8.2.251219**: tutti dichiarati nel progetto e ripristinati automaticamente. Il runtime Windows App SDK è incluso nell’output; non occorre installarlo separatamente.
- Modalità sviluppatore di Windows per sviluppo/distribuzione MSIX. L’eseguibile **unpackaged** predefinito non richiede identità MSIX, certificati o Modalità sviluppatore.
- [tgrep per Windows](https://github.com/microsoft/tgrep/releases): scarica `tgrep-v*-x86_64-pc-windows-msvc.zip`, oppure `tgrep-v*-aarch64-pc-windows-msvc.zip` su ARM64. Estrai il pacchetto e seleziona `tgrep.exe` nelle impostazioni. Test d’integrazione eseguiti con **1.0.4**.

La scelta predefinita del tema è **Sistema**: su Windows scuro l’app parte scura; nelle impostazioni puoi forzare Chiaro o Scuro.

## Compilazione e avvio

I sorgenti sono già completi: **non eseguire `dotnet new` sopra questa cartella**. Dalla root del repository esegui questi comandi PowerShell, uno alla volta:

```powershell
dotnet restore .\tgrep-gui.csproj
```

```powershell
dotnet build .\tgrep-gui.csproj -c Debug --no-restore
```

```powershell
dotnet run --project .\tgrep-gui.csproj --no-build
```

L’output predefinito è `bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64\tgrep-gui.exe`. Si può avviare direttamente; conserva tutti i file della cartella di output accanto all’EXE. `WindowsPackageType=None` e il primo profilo di avvio `Project` rendono **F5 e dotnet run unpackaged**. Apri `tgrep-gui.csproj` in Visual Studio e scegli x64 / profilo Unpackaged.

Su questo workspace è stata preparata anche una copia locale del .NET SDK in `.tools\dotnet`, esclusa da Git. Per usarla nella sessione PowerShell corrente:

```powershell
$env:PATH = "$PWD\.tools\dotnet;$env:PATH"
```

In alternativa lo script seguente sceglie automaticamente l’SDK locale, quando presente:

```powershell
.\scripts\Build.ps1 -Task Run
```

### Template Microsoft, per creare un progetto separato da zero

Il template è documentato qui per riprodurre il punto di partenza, non serve a costruire i sorgenti consegnati. Il pacchetto dei template può avere una versione alpha anche quando il **Windows App SDK dell’app è stabile**.

```powershell
dotnet new install Microsoft.WindowsAppSDK.WinUI.CSharp.Templates --nuget-source https://api.nuget.org/v3/index.json
```

```powershell
dotnet new winui -n WinUiExample -o "$env:TEMP\WinUiExample"
```

### Distribuzione EXE autonoma

```powershell
dotnet publish .\tgrep-gui.csproj -p:PublishProfile=Unpackaged -r win-x64
```

Distribuisci l’intera cartella `artifacts\publish\win-x64`: include .NET e Windows App SDK. tgrep resta un eseguibile esterno configurabile; puoi copiarlo accanto all’app. Non si usa single-file o trimming, per mantenere affidabili XAML e binding.

Per ARM64:

```powershell
dotnet publish .\tgrep-gui.csproj -p:PublishProfile=Unpackaged -p:Platform=ARM64 -r win-arm64
```

### MSIX opzionale

Il manifest, le icone e il profilo MSIX sono inclusi. La distribuzione predefinita resta unpackaged.

```powershell
dotnet publish .\tgrep-gui.csproj -p:PublishProfile=MSIX -p:WindowsPackageType=MSIX -p:Platform=x64 -r win-x64
```

Il profilo produce un pacchetto **non firmato** in `artifacts\msix`. Per installarlo fuori dallo Store occorre firmarlo con un certificato attendibile il cui soggetto corrisponda al Publisher del manifest (`CN=TgrepGui`), oppure usare il flusso Package and Publish di Visual Studio. Il profilo MSIX di avvio richiede una configurazione compilata con `WindowsPackageType=MSIX`; non è il profilo di F5 predefinito. Non viene creato o installato automaticamente alcun certificato.

## Uso

1. Scegli una cartella con **Sfoglia**, incolla un percorso o seleziona una delle ultime 12 cartelle.
2. Inserisci globs facoltativi: `*.cs; *.xaml`, `*.{js,ts}`, oppure `"my files/**"`. Spazi, virgole e punti e virgola separano i filtri; le virgolette preservano gli spazi. Le virgole all’interno di `{...}` o `[...]` sono preservate.
3. Per escludere directory usa `bin/; obj/; node_modules/` (anche `**/bin/**`); per file usa `*.min.js`. I filtri vengono applicati alla ricerca, senza restringere permanentemente l’indice.
4. Inserisci una regex, oppure abilita **Testo letterale**, quindi premi **Cerca** o **Invio** nella casella di ricerca.
5. Scegli un file a sinistra. A destra trovi righe e corrispondenze evidenziate; puoi selezionare testo o più righe con Ctrl/Shift.

| Scorciatoia | Azione |
|---|---|
| Invio, nella casella di ricerca | Avvia ricerca |
| Esc | Annulla ricerca o preparazione indice |
| Ctrl+L | Mostra ricerca e seleziona il testo della query |
| F3 oppure doppio clic sulla riga | Apri nell’editor alla riga |
| Ctrl+C nel pannello righe | Copia le righe selezionate con percorso e numero |

Il menu contestuale di un file offre Apri, Apri cartella contenente e Copia percorso. Il pannello **Log** mostra le ultime 1.000 righe di diagnostica; **Copia log** le copia negli appunti. I log non vengono salvati su disco dall’app.

### Ricerca, indice e cambi di branch

Con **Usa indice** attivo, l’app esegue `tgrep status`. Se manca l’indice esegue `index`, mostrando i messaggi di avanzamento; quindi avvia `serve` e aspetta che l’indice sia pronto. Un server già attivo viene riutilizzato, compreso un server avviato esternamente. La barra di stato riporta PID, porta, file indicizzati, numero di corrispondenze, tempo totale (inclusa preparazione) e avvisi.

Le ricerche successive riutilizzano il server. Il watcher di tgrep gestisce modifiche e cambi di branch: **la GUI non rilancia `index` al cambio di branch**. Dopo un cambio esteso aspetta il watcher e ripeti la ricerca. Se i risultati sembrano obsoleti, **Riavvia server**, poi Cerca; il riavvio non ricostruisce un indice esistente. L’app rifiuta il riavvio di un server esterno e ne spiega il motivo.

Disattivando **Usa indice** la ricerca passa `--no-index` e non costruisce né avvia un server. Eventuali server già avviati restano disponibili. La barra di stato mostra che l’operazione non usa il server.

**Annulla** termina il processo di ricerca o `index` corrente e conserva i risultati parziali con un messaggio esplicito. Un server già avviato resta attivo e viene riutilizzato. Se è ancora impegnato nell’indicizzazione iniziale, una successiva ricerca ne aspetta la conclusione; Esc interrompe questa attesa. Alla chiusura dell’app vengono attese le operazioni correnti e terminati soltanto i processi figli che l’app stessa ha avviato. Non viene mai effettuato un arresto per nome processo.

`--line-buffered` evita il buffering della pipe CLI. L’app visualizza ciascun record JSON appena arriva; tgrep può comunque calcolare internamente una parte dei risultati prima di emetterli. I risultati sono mantenuti in memoria e i controlli di elenco virtualizzano le righe: query che corrispondono a milioni di righe possono richiedere molta RAM.

### Impostazioni ed editor

Il file viene creato al primo avvio in `%AppData%\tgrep-gui\settings.json` e scritto atomicamente. Se è illeggibile l’app lo segnala e non lo sovrascrive automaticamente salvando la cronologia; il pulsante Salva impostazioni permette di sostituirlo esplicitamente.

```json
{
  "TgrepPath": "",
  "IndexPath": "",
  "EditorPath": "",
  "EditorArguments": "\"$FILE\"",
  "Theme": "System",
  "IgnoreCase": true,
  "Literal": false,
  "RecentFolders": []
}
```

Il motore viene cercato nell’ordine: percorso configurato valido, cartella dell’app, PATH. Un percorso personalizzato di indice deve essere **assoluto e dedicato a una sola cartella**. Il campo vuoto lascia che tutti i comandi usino `<folder>\.tgrep`. Le variabili d’ambiente nei percorsi sono supportate. Le modifiche a eseguibile/indice si applicano alla prossima operazione.

Per aprire alla riga esatta imposta il percorso del `.exe` dell’editor:

| Editor | Argomenti |
|---|---|
| Visual Studio Code (`Code.exe`, non `code.cmd`) | `--goto "$FILE:$LINE"` |
| Notepad++ | `-n$LINE "$FILE"` |
| Sublime Text | `"$FILE:$LINE"` |

Senza editor configurato viene aperta l’applicazione Windows associata al file; in questo caso non è possibile imporre il numero di riga. Il template degli argomenti viene suddiviso **prima** di sostituire `$FILE` e `$LINE`, così un nome contenente virgolette o simboli non può introdurre nuovi argomenti.

### Prefill da riga di comando

```powershell
dotnet run --project .\tgrep-gui.csproj --no-build -- --folder .\Core --include-files '*.cs' --exclude-files 'bin/;obj/' --text 'SearchAsync'
```

Gli alias sono `-f`, `-i`, `-e`, `-t`; è accettata anche la forma `--text=valore`. Il prefill non avvia automaticamente la ricerca. Le opzioni CLI della GUI sono distinte dalle opzioni CLI del motore.

## Verifiche

Suite senza dipendenze test esterne, con un eseguibile di test per pipe, argv e cancellazione:

```powershell
dotnet run --project .\Tests\Tests.csproj
```

Per includere i test reali con la release scaricata in questo workspace:

```powershell
.\scripts\Build.ps1 -Task Test -TgrepPath '.\.tools\tgrep\tgrep.exe'
```

La suite usa cartelle temporanee univoche, non repository dell’utente. Verifica parser Unicode/base64, globs, CLI, impostazioni, escaping Windows, cancellazione, stderr abbondante e failure del parser senza deadlock. Con tgrep verifica anche indice iniziale, filtro directory, ricerca senza indice, watcher, riuso, riavvio e sopravvivenza dei server esterni.

Il sorgente del progetto è suddiviso in `Core` (motore indipendente dalla UI), `ViewModels`, `Views`, `Controls`; vedi [ARCHITECTURE.md](ARCHITECTURE.md). Le icone MSIX si rigenerano con `scripts\GenerateAssets.ps1`.

## Fonti

- [Creare una WinUI app con VS 2026 o dotnet new](https://learn.microsoft.com/en-us/windows/apps/get-started/start-here)
- [Windows App SDK 2.x, note della versione stabile](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/release-notes/windows-app-sdk-2-0)
- [Microsoft tgrep: comandi e architettura](https://github.com/microsoft/tgrep)
- [rg-gui: riferimento del flusso d’uso](https://github.com/kcowolf/rg-gui)
