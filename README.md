# custom-windows-mqtt-monitor

Windows-szolgáltatás + tálcás segéd: a PC állapotát MQTT-n küldi a Home Assistantnak **MQTT Discovery**-vel,
így a gép magától megjelenik eszközként („Számítógép”, entitás-előtag: `pc_`), szenzorokkal, gombokkal és egy
**Bekapcsolva** kapcsolóval. A [custom-rpi-mqtt-monitor](https://github.com/patyiandras/custom-rpi-mqtt-monitor)
Windows-os párja; a SmartThings „PC Control” (Computer) eszközt váltja ki.

Az elődje a 2018-as `TcpListener` (Windows-szolgáltatás, HTTP a 7268-as porton) és `ShutdownDaemon`
(leállítás-visszaszámláló „Cancel” gombbal) volt — a git-előzmény első commitja. Azt a két dolgot,
ami bevált, megtartja: a szolgáltatás rendszerindításkor indul, és a leállítás előtt a felhasználó
egy visszaszámlálóval megszakíthatja.

Futtatókörnyezet: .NET Framework 4.8 (minden Windows 10/11-en gyárilag megvan), egyetlen függőség az
`MQTTnet.dll`. Nyitott port nem kell: a gép csak kifelé csatlakozik a brokerhez (a régi
SmartThings-végpont kivétel, lásd lent).

## Felépítés

| Rész | Hol fut | Feladat |
|---|---|---|
| `WinMqttMonitor.exe` (szolgáltatás) | session 0, LocalSystem, automatikus indulás | adatgyűjtés, MQTT, parancsok, leállítás/újraindítás/alvás végrehajtása, régi HTTP-végpont |
| `WinMqttMonitorTray.exe` (tálcás segéd) | minden bejelentkezett felhasználó sessionjében | visszaszámláló-ablak „Mégse” gombbal, zárolás, képernyő ki, értesítés; jelenti a tétlenséget és a zárolást |

A kettő a `C:\ProgramData\WinMqttMonitor\ipc` mappán keresztül beszél (a szolgáltatás nem tud ablakot
nyitni a felhasználónak, a segéd pedig nem lát minden rendszeradatot).

## Mit lát a HA?

| Entitás | Megjegyzés |
|---|---|
| **Bekapcsolva** (kapcsoló) | be = a monitor online; **kikapcsolás = leállítás visszaszámlálóval**. Bekapcsolni a HA tud Wake-on-LAN-nal (lásd lent). Alváskor és leállításkor azonnal „ki”, áramszünet/fagyás esetén a keepalive (45 mp) után. |
| CPU-terhelés, Memória-használat (attr.: GB), Véglegesített memória (commit) | 30 mp-enként |
| CPU hőmérséklet (ACPI) | csak ha a gép ACPI-hőzónát ad (sok asztali alaplap nem; ilyenkor nem jelenik meg) |
| GPU hőmérséklet / terhelés / memória | csak NVIDIA-kártyánál (`nvidia-smi`) |
| Hálózat le / fel (kB/s), IP-cím (attr.: MAC, csatoló, link-sebesség), WiFi-jel | WiFi csak ha azon van |
| Akkumulátor, Töltés | csak laptopon |
| `Lemez C:`, `Lemez D:`, … telítettség % (attr.: szabad/összes GB, címke, fájlrendszer) | minden belső meghajtó, 5 percenként; új/eltűnt meghajtó magától követi |
| Bejelentkezett felhasználó (attr.: session, távoli-e, fut-e a segéd) | konzol, ha ott senki: aktív RDP-session |
| **Használatban** (occupancy), **Zárolva**, Tétlenség (perc) | a segédtől; használatban = bejelentkezve, nincs zárolva, 5 percen belül volt bevitel |
| Memória top program (attr.: a 10 legtöbb memóriát használó program, név szerint összevonva) | percenként |
| Windows-frissítések (attr.: címek), Újraindítás szükséges | a Windows saját utolsó ellenőrzéséből, 4 óránként (nem indít letöltést) |
| Bekapcsolás ideje, Windows-verzió, Monitor verzió | |
| Utolsó parancs, Függő művelet (attr.: határidő) | audit + a futó visszaszámlálás |
| Gombok: **Leállítás**, **Újraindítás**, **Alvó mód**, **Hibernálás**, **Leállítás megszakítása**, **Zárolás**, **Képernyő ki**, Adatok frissítése | az alvó mód / hibernálás csak ha a gép támogatja |
| **Üzenet a képernyőre** (szöveg) | Windows-értesítésként jelenik meg a felhasználónak |
| `Szolgáltatás <név>` fut-e + `<név> újraindítása` | a configban felsorolt Windows-szolgáltatásokhoz |

### Leállítás, újraindítás, alvás

A HA-ból kért energiaművelet nem azonnali: a felhasználó képernyőjének jobb alsó sarkában megjelenik
egy (fókuszt nem lopó) ablak „Leállítás 30 mp múlva” felirattal és **Mégse** gombbal; a HA-ból a
**Leállítás megszakítása** gomb ugyanezt teszi. Ha senki nincs bejelentkezve, a határidő után
végrehajtódik. A leállítás nem kényszerített (`shutdown /s /t 0`): ha egy program mentetlen
dokumentum miatt megakasztja, a gép 3 perc után újra „online”-nak jelenti magát.

Megjegyzés: a **Gyors rendszerindítás** (Fast Startup) bekapcsolt állapotában a Start menüs
„Leállítás” valójában hibernálás — a Bekapcsolás ideje ilyenkor nem változik. A HA-ból kért
leállítás (`shutdown /s`) teljes leállítás.

## Telepítés

1. Mosquitto add-on → *Configuration → Logins*: saját felhasználó a PC-nek (pl. `pc`).
2. Fordítás (bármelyik gépen, ahol van .NET SDK): `.\build.ps1` → a `publish\` mappa a telepítőcsomag.
3. A cél-PC-n, **rendszergazdai PowerShellben**, a `publish\` mappából (vagy a repó gyökeréből):
   ```powershell
   Set-ExecutionPolicy -Scope Process Bypass
   .\install.ps1
   ```
   Az első futás létrehozza a `C:\ProgramData\WinMqttMonitor\config.json`-t és megnyitja Jegyzettömbben:
   írd bele az MQTT-jelszót (és ha kell, a gép nevét / `id`-jét), majd futtasd újra az `install.ps1`-et.
   A config.json-t csak a SYSTEM és a rendszergazdák olvashatják.

Az install.ps1 ezen felül:
- leállítja és törli a régi **„TCP Listener service”**-t, kilövi a `ShutdownDaemon`-t, és törli az
  automatikus indítását (Run-kulcs, Indítópult-parancsikon; ütemezett feladatnál csak figyelmeztet);
- regisztrálja a `WinMqttMonitor` szolgáltatást (automatikus indulás, hibánál 10/30/60 mp után újraindul);
- a tálcás segédet minden bejelentkezéshez beállítja (HKLM\…\Run) és el is indítja;
- a régi SmartThings-végponthoz tűzfalszabályt nyit (privát hálózat, TCP 7268).

Frissítés: ugyanígy `.\install.ps1` (a configot nem bántja). Eltávolítás: `.\uninstall.ps1`
(a HA-ból is törli az eszközt; `-Purge` a configot és a naplót is).

Napló: `C:\ProgramData\WinMqttMonitor\logs\monitor.log` (a tálca-ikon menüjéből is megnyitható).

## Beállítás (`config.json`)

| Kulcs | Alapérték | |
|---|---|---|
| `mqtt.host`, `port`, `username`, `password` | `192.168.0.137`, `1883` | a HA Mosquitto |
| `mqtt.tls`, `tls_insecure` | `false` | TLS (8883) |
| `device.id`, `device.name` | `pc`, `Számítógép` | az `id` az entitás-előtag (`sensor.pc_cpu_terheles`) és a topic része |
| `intervals.fast` / `slow` / `processes` / `updates` / `session` | 30 / 300 / 60 / 14400 / 5 mp | |
| `power.countdown` | 30 | a visszaszámlálás hossza (mp) |
| `power.allow_shutdown` / `allow_restart` / `allow_sleep` / `allow_hibernate` | `true` | a tiltott művelet gombja meg sem jelenik |
| `power.command_grace` | 60 | indulás után ennyi mp-ig nem fogad energiaparancsot |
| `power.command_cooldown` | 10 | ugyanaz a parancs legfeljebb ennyi mp-enként |
| `services.names` | `[]` | figyelt Windows-szolgáltatások (szolgáltatásnév, pl. `"Spooler"`) |
| `services.restart` | `true` | újraindító gomb a figyelt szolgáltatásokhoz |
| `legacy_http.enabled`, `port` | `true`, `7268` | a SmartThings „PC Control” driver régi végpontja (`/?Ping`, `/?ST.PCPower.Shutdown`) |
| `sensors.cpu_temp` / `gpu` / `windows_update` | `true` | kikapcsolhatók |

Módosítás után: `Restart-Service WinMqttMonitor`.

## Bekapcsolás a HA-ból (Wake-on-LAN)

A kapcsoló „be” parancsa a `win-mqtt-monitor/pc/cmd/power` topicra `ON`-t küld — ezt a kikapcsolt
gép nem hallja, ezért a HA-nak kell felébresztenie. Kell hozzá a BIOS-ban és a hálózati kártya
energiagazdálkodásában engedélyezett Wake-on-LAN. HA-csomag (pl. `packages/szamitogep.yaml`; a MAC a
`sensor.pc_ip_cim` `mac` attribútumából olvasható ki, amíg a gép be van kapcsolva):

```yaml
wake_on_lan:

automation:
  - id: szamitogep_wol
    alias: Számítógép bekapcsolása (Wake-on-LAN)
    triggers:
      - trigger: mqtt
        topic: win-mqtt-monitor/pc/cmd/power
        payload: "ON"
    actions:
      - action: wake_on_lan.send_magic_packet
        data:
          mac: "aa:bb:cc:dd:ee:ff"
```

## Átállás a SmartThingsről

A régi végpont (7268) az új szolgáltatásban tovább él, így a SmartThings „Computer” eszköz és a
rutinjai az átállás alatt is működnek (a leállítás most már ugyanazzal a visszaszámlálóval megy).
Ha a HA-s automatizmusok átkerültek a `switch.pc_bekapcsolva`-ra, a configban
`"legacy_http": { "enabled": false }`, újra `install.ps1` (a tűzfalszabályt is leveszi), és a
SmartThings-eszköz törölhető.

## Biztonság

- **Csak fix parancsok.** Leállítás, újraindítás, alvás, hibernálás, megszakítás, zárolás,
  képernyő ki, értesítés, a *configban felsorolt* szolgáltatások újraindítása. Tetszőleges parancsot
  nem futtat, payloadból nem épít parancssort.
- A megőrzött (retained) parancsokat eldobja, az indulás utáni 60 mp-ben nem fogad energiaparancsot,
  és ugyanazt a parancsot legfeljebb 10 mp-enként hajtja végre.
- Az energiaműveletek mindig visszaszámlálással mennek, a gépnél megszakíthatók.
- A régi HTTP-végpont hitelesítés nélküli (ilyen volt eddig is) — csak az átállás idejére; a tűzfal
  csak privát/domain hálózatra nyitja.

## Fejlesztés, tesztelés

```powershell
dotnet build WinMqttMonitor.sln -c Release
# MQTT nélkül: kiírja a discovery-t és egy teljes adatkört
src\WinMqttMonitor\bin\Release\WinMqttMonitor.exe --dump
# valódi brokerrel, konzolra is naplózva
src\WinMqttMonitor\bin\Release\WinMqttMonitor.exe --console
```

A `WINMQTTMONITOR_DATA` környezeti változó a `C:\ProgramData\WinMqttMonitor` helyett más
adatkönyvtárat ad (config, napló, ipc) — fejlesztői gépen így nem kell rendszergazda. Teszteléshez a
`"power": { "simulate": true }` az energiaműveleteket csak naplózza.
