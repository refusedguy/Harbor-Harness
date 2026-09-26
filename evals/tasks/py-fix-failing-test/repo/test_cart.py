"""Tests for cart.checkout (do not modify)."""
import unittest

from cart import checkout


class TestCheckout(unittest.TestCase):
    def test_no_discount(self):
        self.assertEqual(checkout([10, 20], 0), 30)

    def test_discount_once(self):
        self.assertEqual(checkout([100], 10), 90)

    def test_empty(self):
        self.assertEqual(checkout([], 50), 0)


if __name__ == "__main__":
    unittest.main()
