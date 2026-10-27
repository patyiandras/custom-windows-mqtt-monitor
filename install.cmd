@echo off
rem Dupla kattintas: rendszergazdakent (UAC), a vegrehajtasi hazirend megkerulesevel futtatja a ps1-et.
powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Process powershell -Verb RunAs -ArgumentList '-NoProfile -ExecutionPolicy Bypass -NoExit -File \"%~dp0install.ps1\"'"
