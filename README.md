# OP Auto Clicker

Humanizovaný autoclicker pro Windows: kliká v náhodných dávkách s náhodnými pauzami a volitelně jemně hýbe kamerou, aby to působilo přirozeně.

## Build

Stačí Windows (používá `csc` z .NET Frameworku 4, který je součástí systému):

```
build.bat
```

Vznikne `OpAutoClicker.exe`.

## Ovládání

- **F6** (nebo vlastní klávesa) či tlačítko Start/Stop zapíná a vypíná klikání.
- Kliká jen když je v popředí okno, jehož název obsahuje text z pole „Jen v okně“ (výchozí `Minecraft`; prázdné = všude).
- Minimalizace schová okno do systémové lišty (zelená tečka = běží, šedá = stojí). Dvojklik ikonu vrátí, pravé tlačítko nabídne Start/Stop a Konec.
- Nastavení se při zavření uloží do `OpAutoClicker.ini` vedle exe a při dalším spuštění se načte.

## Nastavení

| Pole | Význam |
|---|---|
| Kliků v dávce | kolik kliků jde po sobě |
| CPS v dávce | tempo uvnitř dávky (0,1–30) |
| Pauza mezi dávkami (s) | jak dlouho po dávce nekliká (0 = bez přestávek) |
| Tlačítko | levé / pravé |
| Akce | klikat, nebo držet tlačítko stisknuté (kopání, AFK farmy) |
| Pohyb kamery, Síla kamery | zapnutí a rozsah pohybu (cca 6–120 px) |
| Pauza kamery (s) | klid mezi sériemi pohybů kamery |
| Spouštění | přepínat klávesou, nebo kliká jen při držení zvolené klávesy |
| Klávesa Start/Stop | kliknout a stisknout novou klávesu (i s Ctrl/Shift/Alt), Esc = zrušit |
| Klávesa pro držení | klávesa, kolečko nebo boční tlačítko myši (výchozí Myš 4) |
| Zastavit po | automatické zastavení po N minutách / N klicích (0 = bez limitu) |

V režimu „Jen při držení“ klávesa Start/Stop klikání odjistí (stav „Připraveno“) a kliká se jen, dokud držíš klávesu pro držení.

Příklady: jeden klik za ~5 s = dávka 1–1, pauza 4–6. Plynulé klikání = pauza 0–0.

Kamera se nehýbe, když je vidět kurzor (otevřené menu / inventář).

## Upozornění

Většina multiplayer serverů autoclickery zakazuje a anticheaty je umí odhalit i přes náhodnost. Používáš na vlastní riziko.
