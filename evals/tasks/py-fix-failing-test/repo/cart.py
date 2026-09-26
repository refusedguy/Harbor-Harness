"""Tiny checkout fixture (contains a bug)."""


def checkout(prices, discount_pct):
    subtotal = sum(prices)
    discount = subtotal * discount_pct / 100
    return subtotal - discount - discount
