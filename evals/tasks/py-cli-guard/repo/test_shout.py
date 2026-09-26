"""Tests for shout (do not modify)."""
import unittest

from shout import render, main


class TestShout(unittest.TestCase):
    def test_render(self):
        self.assertEqual(render("  hello  "), "HELLO!")

    def test_main_word(self):
        self.assertEqual(main(["hi"]), "HI!")

    def test_main_empty(self):
        self.assertEqual(main([]), "!")


if __name__ == "__main__":
    unittest.main()
