In the workspace directory there are two Python files: `store.py`
(a tiny store helper with a bug) and `test_store.py` (its unit tests,
currently FAILING).

Fix the bug in `store.py` so that all tests in `test_store.py` pass.
You may run the tests yourself to iterate:

```sh
python3 -m unittest -v
```

Hard constraint: the public API must not change — `add_item(cart, name,
price)` and `total(cart)` keep exactly these names, parameters, and
parameter order. Do NOT modify `test_store.py`. Do not create any other
files.
