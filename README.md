CLR Tools
===

Tools for developers using .NET.

Allocations
---

    Allocations.exe assembly type method

This tool displays all functions that allocate objects.

Of course, that's too much data, so instead it only lists functions that allocate
that are called from a root `method` of a `type`. You also need to specify the `assembly`
that contains the type.
