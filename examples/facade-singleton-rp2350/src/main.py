# Regression: a module-level singleton (`wifi_mod.radio`) whose class comes through a
# facade re-export must dispatch to the concrete class, not the nonexistent "radio_light".
from wifi_mod import radio

def main():
    radio.light()              # was: "call to undefined function 'radio_light'"
    while True:
        pass
