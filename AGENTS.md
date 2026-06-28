<!-- >>> csharp-style (managed — re-init overwrites this block) >>> -->
# Agent guide — C#

Style, naming, and member order are enforced by `.editorconfig` + StyleCop, so
the build will flag them — no need to memorize them here.

**When all your edits are done**, run once to auto-format the files you changed:

```
csharp-style run --no-reorder
```

`--no-reorder` runs the format pass only — it's fast and needs no extra tooling.
Member ordering is enforced separately by the build, so you don't need the
slower reorder pass here. Run this once at the end, not after every edit.

## Two things the formatter can't catch — get them right while writing

- **EF Core queries:** for case-insensitive search use `x.ToLower().Contains(y)`
  or `EF.Functions.Like(x, $"%{y}%")`. Never `Contains(y, StringComparison...)`,
  `ToLowerInvariant()`, or `ToString(culture)` in a query — EF can't translate
  them and the query fails at runtime.
- **EF entities / Blazor `*.razor.cs`:** don't delete a private parameterless
  ctor or backing fields (EF uses them via reflection), and don't rename/remove
  members that `.razor` markup binds to. These compile but break at runtime.
<!-- <<< csharp-style (managed) <<< -->
