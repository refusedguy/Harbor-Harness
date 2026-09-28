"""Tests for store.py (do not modify)."""

import unittest

from store import add_item, total


class StoreTests(unittest.TestCase):
    def test_free_sample_allowed(self):
        cart = add_item([], "sample", 0)
        self.assertEqual(total(cart), 0)

    def test_normal_add(self):
        cart = add_item([], "book", 25)
        self.assertEqual(total(cart), 25)

    def test_negative_rejected(self):
        with self.assertRaises(ValueError):
            add_item([], "refund", -5)


if __name__ == "__main__":
    unittest.main()
