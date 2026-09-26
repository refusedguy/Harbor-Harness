"""Tiny shout CLI fixture (crashes on empty input)."""
import sys


def render(text):
    return text.strip().upper() + "!"


def main(argv):
    return render(argv[0])


if __name__ == "__main__":
    print(main(sys.argv[1:]))
