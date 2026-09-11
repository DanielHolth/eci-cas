@echo off
rem Double-clickable entry point. Everything real is in scripts\start.ps1;
rem this exists so the launcher is reachable without a PowerShell prompt and
rem without arguing with the execution policy.
rem
rem Arguments pass straight through:  start.cmd -Tier Free
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\start.ps1" %*
