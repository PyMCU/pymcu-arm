# Read ADC channel 0 (GP26) and drive the LED (GP25) when above mid-scale.
from pymcu.hal.gpio import Pin
from pymcu.hal.adc import AnalogPin


def main():
    led = Pin(25, Pin.OUT)
    adc = AnalogPin(0)
    while True:
        v = adc.read()
        if v > 2048:
            led.high()
        else:
            led.low()
