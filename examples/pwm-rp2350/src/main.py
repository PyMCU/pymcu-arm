# Drive GP2 with a 5 kHz PWM at 50% duty.
from pymcu.hal.pwm import PWM


def main():
    pwm = PWM(2, 5000, 32768)
    while True:
        pass
