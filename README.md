# Hlas

Prenosná Windows aplikácia na čítanie textu zo schránky. C# / WPF, vizuálny štýl inšpirovaný Lumou. Windows 10 (2004+) / Windows 11, x64. Na používanie netreba inštalovať .NET ani FFmpeg.

## Spustenie

Spusti `dist\Hlas.exe`. Aplikácia zostane pri hodinách. Skopíruj text a stlač **Ctrl + Alt + V**. Prvá krátka časť sa začne automaticky prehrávať, ďalšie vznikajú na pozadí. Celkový text nemá administratívny limit 4 000 či 20 000 znakov. Delenie uprednostňuje odseky a celé vety; extrémne dlhé slová rozdelí bez poškodenia Unicode.

Počas prípravy, prehrávania aj pauzy skratka iba pípne. **Pauza** pozastaví zvuk, príprava pokračuje do naplnenia zásoby troch častí. **Zastaviť** zruší čítanie aj ďalšie požiadavky. Už odoslané volania môžu byť účtované. Po dohraní alebo zastavení možno načítať nový text.

Prehrávač je presúvateľný, jemne priehľadný a nad ostatnými oknami. Pri skratke nepreberá fokus. Krížik ho iba skryje, zvuk pokračuje. Znova ho otvoríš dvojklikom na ikonu pri hodinách. Rýchlosť 0,5–2× sa mení priebežne bez opakovania textu a bez zmeny výšky hlasu. Pripravené časti sa spájajú v jednom zvukovom prúde; pri pomalej službe sa zobrazí čakanie a prehrávanie pokračuje, keď príde ďalšia časť.

## Nastavenia a prenos

Nastavenia otvoríš cez ikonu ozubených koliesok alebo menu pri hodinách. API kľúč možno napísať alebo načítať zo súboru. Voľba hlasového modelu, hlasu a textového modelu obsahuje pribalený katalóg aj jeho obnovenie zo služby; polia umožňujú vlastné ID. Príprava textu je predvolene zapnutá a prebieha automaticky bez náhľadu. Očistí formátovanie, kód a zložité vzorce nahradí poznámkou. Model môže urobiť chybu pri úprave textu.

Predvolené modely overené v katalógu 21. 10. 2026:

- Hlas: `google/gemini-3.8-flash-lite-tts`, hlas `Kore`.
- Príprava: `google/gemini-3.5-flash-lite`.

Vedľa EXE sú prenosné používateľské súbory:

- `openrouter.api` — API kľúč v obyčajnom texte, podľa požiadavky používateľa.
- `hlas.settings.json` — nastavenia, rýchlosť a poloha prehrávača.
- `hlas.usage.json` a `.bak` — história použitia a jej záloha.

Prenášaj celý priečinok. Používateľské súbory nie sú súčasťou verejného vydania. Voľba spustenia pri prihlásení zapisuje aktuálnu cestu do Windows; po presunutí aplikácie ju znova ulož v nastaveniach. Priečinok aplikácie musí povoľovať zápis. Prázdny API kľúč nevyvolá platené volanie. Schránka sa číta iba na výslovnú skratku alebo príkaz z menu a nemení sa.

## Použitie a ceny

Karta **Použitie a ceny** ukazuje celkové zistené náklady, dnešok, aktuálny mesiac, rozdelenie text/audio, modely a posledných 100 volaní. Súčet zahŕňa celú uloženú históriu. Ide o použitie tejto aplikácie, nie celého účtu OpenRouter.

Ceny sú presné desatinné údaje `usage.cost` alebo `generation.data.total_cost`. Nedostupná cena nie je nula; nezistené volania sú viditeľne uvedené mimo súčtu. Zrušené a chybové volania zostávajú v histórii. Oneskorené ceny sa overujú na pozadí; tlačidlo **Dočítať chýbajúce ceny** kontroluje najnovších 100 nezistených ID bez opakovania plateného generovania. Po zmene API kľúča nemusia byť staršie ID prístupné. História neobsahuje text zo schránky ani kľúč.

## Aktualizácie

Verejné vydania: https://github.com/enthilza/Hlas-Clipboard/releases

**Aktualizovať** je v prehrávači, nastaveniach aj menu pri hodinách. Používa verejné release súbory bez GitHub REST API a bez účtu. RSA/SHA-256 podpis manifestu sa overí zabudovaným verejným kľúčom, následne veľkosť, architektúra a SHA-256 EXE. Samotný checksum nie je jedinou ochranou. Nová verzia sa nainštaluje až po potvrdení reštartu; aktuálne čítanie sa ukončí. Aktualizátor ponechá zálohu do potvrdenia úspešného štartu a pri zlyhaní spustenia obnoví pôvodné EXE. Kľúč, nastavenia a história zostávajú zachované.

Dočasné súbory aktualizátora sú v `%LOCALAPPDATA%\HlasClipboard\Updates`, diagnostika chyby v `update-error.txt`. Privátny vydavateľský kľúč (odlišný od OpenRouter kľúča) je ako v Lume chránený DPAPI mimo projektu v `%LOCALAPPDATA%\HlasClipboardPublisher`. Nikdy sa nepublikuje. Aktualizačný podpis nie je Authenticode podpis EXE.

## Zostavenie

Potrebný je .NET 10 SDK na Windows. `build.cmd -NoPause -Verify` vytvorí samostatné `dist\Hlas.exe`, spustí offline kontroly a vymaže presne vlastný dočasný priečinok `.build`. Zdieľaná cache balíkov ostáva mimo projektu. Existujúce používateľské súbory sa neprepisujú. Ak `dist\openrouter.api` ešte neexistuje, build skopíruje kľúč z koreňa projektu. `-OutputDirectory` umožňuje iný výstup. Spustené EXE pred bežným buildom ukonči cez menu pri hodinách.

`scripts/prepare-release.ps1` vytvorí `update.json`, `update.sig`, `SHA256SUMS.txt`. Každá zmena EXE vyžaduje nový podpis. Existujúci vydavateľský kľúč nesmie byť nahradený novým. `--verify-package <priečinok>` overí podpísaný balík a `--check-update <report.txt>` overí verejný aktualizačný kanál bez inštalácie.

`Hlas.exe --self-test <report.json>` spúšťa lokálne testy bez platených volaní. `--live-test <report.json>` je výslovne platený vývojový test s krátkym pevným textom. Bežný build ho nespúšťa.

## Overenie a obmedzenia

Offline testy overujú poradie a úplnosť delenia, Unicode, zásobu audia, pauzu, zastavenie, chyby bez opakovania, presné ceny a trvalú históriu, rýchlosť a frekvenciu syntetického tónu, skratky, podpisy, sťahovanie, obnovu EXE a vykreslenie rozhrania. Živý test oboch predvolených modelov vytvoril dekódovateľné audio 6,64 s za $0.0011955. Nejde o posluchové hodnotenie výslovnosti. Konkrétne výsledky sú v lokálnom `test-results`.

Syntéza nie je streamovaná po jednotlivých vzorkách zo siete: prehrá sa prvá kompletná krátka časť. Medzi samostatnými volaniami môže poskytovateľ mierne zmeniť prednes. Pri výpadku sa čítanie zastaví, platené volania sa automaticky neopakujú. Rozpracované audio sa drží iba v pamäti a po ukončení sa neobnovuje.

Zvuk využíva NAudio a SoundTouch.Net; licencie sú v `licenses` a `THIRD-PARTY-NOTICES.md`. Zdroj aplikácie možno znovu zostaviť aj s upravenou knižnicou SoundTouch.Net. Používateľské nastavenia a projektové poznámky nepatria do verejného repozitára.
