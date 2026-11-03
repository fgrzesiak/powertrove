# Browser Companion privacy disclosure — draft for Chrome/Edge listings

This is proposed public-facing copy for the desktop and browser stores. It is not
a claim that an extension has already been reviewed or published.

## English

**PowerTrove Browser Companion** is an optional Chrome/Edge extension that connects
to the PowerTrove desktop app locally. It accesses the current browser page only
when the user triggers a PowerTrove action or opens the desktop palette.
With optional website access granted, it may read the current page's selected
text, page title, URL, visible article content, HTML tables and image/link URLs
for the requested action. Without that permission, it can provide limited
metadata only. Incognito and protected browser pages are excluded.

Browser contents and selected text are processed locally and are not submitted
to a PowerTrove cloud service. Explicit image downloads use the images' normal
hosting servers. The "recent download" action inspects the browser's last
downloads only when clicked and forwards one verified local filename to the
desktop; download history is not persistently collected. Native messaging uses
a user-approved extension identity and same-user Windows IPC. An optional
persistent connection holds no browsing history or clipboard monitoring.
On user confirmation, PNG/MHTML captures may be streamed to the local desktop
and into an isolated signed pack worker using bounded temporary buffers. The
signed pack prints such a saved copy to PDF with a sandboxed local Edge process
with network and scripts disabled; it does not use a browser debugger permission
or submit page content to an online service.

The extension requests required permissions for native messaging, downloads,
user-initiated page scripting, clipboard write, and context menus. Access to all
HTTP(S) sites is an **optional** permission used for reading the current page
on the PowerTrove hotkey. Users may revoke this permission and disconnect from
the desktop at any time.

## Deutsch

Der **PowerTrove Browser Companion** ist eine optionale Erweiterung für Chrome
und Edge, die lokal mit der PowerTrove-Desktop-App kommuniziert. Die Erweiterung
liest Seiteninhalte nur, wenn eine PowerTrove-Aktion ausdrücklich aufgerufen
wird oder die Desktop-Palette den aktuellen Kontext abfragt. Mit optional
erteiltem Seitenzugriff kann sie markierten Text, Seitentitel, URL, Artikel,
HTML-Tabellen sowie Bild- und Linkadressen der aktuellen Seite verarbeiten.
Ohne diese Berechtigung stehen nur eingeschränkte Metadaten zur Verfügung.
Inkognito- und geschützte Browserseiten werden ausgeschlossen.

Seiteninhalte und Textauswahlen werden lokal verarbeitet und nicht an einen
PowerTrove-Cloud-Dienst übertragen. Beim Herunterladen von Bildern gelten die
normalen Verbindungen zu den jeweiligen Bildservern. Die Aktion für den
letzten Download prüft den Downloadverlauf nur nach einem Klick und übergibt
eine lokale Datei an die Desktop-App; der Verlauf wird nicht dauerhaft gesammelt.
Die Verbindung nutzt eine vom Nutzer bestätigte Erweiterungs-ID und
Windows-Kommunikation innerhalb desselben Benutzerkontos. Nach Bestätigung
können PNG-/MHTML-Aufnahmen begrenzt an den isolierten Pack-Worker übertragen
werden. Der signierte Pack druckt eine solche gespeicherte Kopie mit einem
lokalen, netzwerk- und skriptgesperrten Edge-Prozess als PDF. Eine
Debugger-Berechtigung oder ein Online-PDF-Dienst wird nicht verwendet.

Berechtigungen für Native Messaging, Downloads, ausdrücklich ausgelöste
Seitenskripte, Schreiben in die Zwischenablage und Kontextmenüs sind
funktionsgebunden. Der Zugriff auf HTTP(S)-Seiten ist optional und kann
jederzeit widerrufen werden. Auch die Desktop-Verbindung lässt sich trennen.

## Verification before publication

Confirm this text against actual packaged permissions, browser store policies,
telemetry implementation, repository website content and supported browsers.
Any subsequent analytics or remote service would require a new privacy review.
