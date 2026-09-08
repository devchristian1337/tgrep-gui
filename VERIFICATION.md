# Verifiche della consegna

Eseguite su Windows x64 l’8 settembre 2026.

| Controllo | Esito |
|---|---|
| .NET SDK | 10.0.400, installato localmente in `.tools/dotnet` |
| Windows App SDK | 2.4.0 stabile, ripristinato da NuGet |
| Build Debug unpackaged | Superata, 0 errori e 0 warning |
| `dotnet run`, profilo predefinito Project | App avviata correttamente senza identità MSIX |
| Publish Release x64 autonomo | Superato; EXE avviato direttamente dalla cartella di pubblicazione |
| MSIX x64 opzionale | Generato; non firmato e non installato |
| Suite core/processi + tgrep 1.0.4 reale | **36 controlli superati** |
| GUI: ricerca e risultati | Verificati file, righe, evidenziazione dopo accenti/emoji, conteggi, PID/porta |
| GUI: impostazioni | Verificata navigazione e corretto contenimento del layout nella finestra |
| GUI: Ctrl+L | Verificato ritorno dalla pagina impostazioni alla casella di ricerca |
| Chiusura GUI | Verificata uscita normale e terminazione del server figlio di prova |
| Popup log | Verificato pulsante Copia log subito visibile e fermo durante lo scorrimento verticale; nessuno scorrimento orizzontale |
| Copia log dopo lo scorrimento | Verificati negli appunti sia il primo messaggio di indice sia l’ultimo messaggio di ricerca |

La suite controlla anche il riuso del PID tra ricerche, aggiornamento watcher dopo una modifica, riavvio del solo figlio posseduto, sopravvivenza del server esterno, timeout/cancellazione, pipe stderr abbondante, JSON malformato senza deadlock, argv Windows con spazi/virgolette/backslash, parser UTF-8/base64, filtri e regex non valide. Usa soltanto fixture temporanee.

La release Windows di tgrep usata per i test è stata confrontata con il digest SHA-256 pubblicato nell’API GitHub della release: `9b8d5488b1c342c10f222806de84a78049e8d8e8bdd35e34a0f872560c700b56`.

Le configurazioni ARM64 sono incluse; non è stata eseguita una prova runtime su hardware ARM64. Firma e installazione del pacchetto MSIX richiedono il certificato del distributore. Questi passaggi non sono necessari per l’EXE unpackaged consegnato.
