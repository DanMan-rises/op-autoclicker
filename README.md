# OP Auto Clicker

Humanizovaný autoclicker pro Windows: kliká v náhodných dávkách s náhodnými pauzami a volitelně jemně hýbe kamerou, aby to působilo přirozeně.

## Build

Stačí Windows (používá `csc` z .NET Frameworku 4, který je součástí systému):

```
build.bat
```

Vznikne `OpAutoClicker.exe`.

## Ovládání

- **F6** nebo tlačítko Start/Stop zapíná a vypíná klikání.
- Kliká jen když je v popředí okno, jehož název obsahuje text z pole „Jen v okně“ (výchozí `Minecraft`; prázdné = všude).

## Nastavení

| Pole | Význam |
|---|---|
| Kliků v dávce | kolik kliků jde po sobě |
| CPS v dávce | tempo uvnitř dávky (0,1–30) |
| Pauza mezi dávkami (s) | jak dlouho po dávce nekliká (0 = bez přestávek) |
| Tlačítko | levé / pravé |
| Pohyb kamery, Síla kamery | zapnutí a rozsah pohybu (cca 6–120 px) |
| Pauza kamery (s) | klid mezi sériemi pohybů kamery |

Příklady: jeden klik za ~5 s = dávka 1–1, pauza 4–6. Plynulé klikání = pauza 0–0.

Kamera se nehýbe, když je vidět kurzor (otevřené menu / inventář).

## Upozornění

Většina multiplayer serverů autoclickery zakazuje a anticheaty je umí odhalit i přes náhodnost. Používáš na vlastní riziko.
