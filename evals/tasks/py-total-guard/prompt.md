In the workspace directory there is a Python file `shop.py` with a
function `total(prices)` that sums a list of prices but currently adds
negative values as-is (a refund bug: negatives must be ignored).

Change `total` so that negative prices are skipped (treated as zero)
while non-negative prices sum normally. Keep the function signature.
Do not add dependencies. Do not create any other files.
