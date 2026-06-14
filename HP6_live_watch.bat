@echo off
cd /d "%~dp0"
python ".\keil_live_watch.py" --config ".\HP6_A_watch_config.json" %*
pause
