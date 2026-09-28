"""Tiny store fixture (contains a bug)."""


def add_item(cart, name, price):
    if price <= 0:
        raise ValueError("price must be non-negative")
    cart.append({"name": name, "price": price})
    return cart


def total(cart):
    return sum(item["price"] for item in cart)
