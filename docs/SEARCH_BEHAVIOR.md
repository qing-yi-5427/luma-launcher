# Search behavior, shortcuts, and customization

This reference preserves the detailed behavior previously documented in the main README.

## Search ordering and scope

The **排序** button above results is available in quick and expanded views. Its
label always shows the saved choice; the menu marks the current option. It shares
`ResultSort` with Settings, including an already-open Settings window. Changing
order restarts the query, resets the loading limit, and keeps the current type
filter. Load more and expand continue using that order.

- **Smart / relevance / usage and favorites** rank the recalled candidate set;
  they do not promise the globally most relevant or most frequently used file.
  Fuzzy/Pinyin fallback recall is available in these modes.
- **Name / size / modified** pass Everything's SDK sort BEFORE the result limit.
  File-system results therefore come from the full literal Everything match set,
  not from sorting metadata for an earlier 64-result preview. These modes do not
  add independent fuzzy/Pinyin fallback queries (use Everything syntax explicitly
  if needed), or discard provider matches with a local fuzzy filter. Name uses
  Everything's collation, which may differ from .NET's locale-aware app ordering.
- In mixed **All** results, Everything matches retain provider order and come
  **before applications and built-in suggestions**. These remaining local results
  are ordered by name (descending only for name Z–A); the Application filter remains
  available to reach them without paging through files. This is not a single
  cross-provider global ordering. Explicit calculator/URL/command queries retain
  their built-in behavior. Empty search remains recent usage, regardless of sort.
- **Size** compares files only. Folders have no aggregate size: they follow all
  matching files in name A–Z order in both directions; Folder-only is always A–Z.
  Applications/tools have no indexed size/time and follow provider results. No
  synchronous per-file filesystem I/O is used for sorting. Indexed file values
  missing from Everything follow its native ordering, not an invented zero size.
  **Modified** includes indexed files and folders in Everything's native order.
- Expanding/Load more re-queries a larger top-N. Ties use Everything's native order;
  an index changing between requests can naturally change the prefix. Counts and
  has-more describe provider matches, not an immutable snapshot.

### Search-hit highlighting

Quick and expanded results share case-insensitive highlighting in names, paths,
and the selected detail title/location. All literal occurrences take priority
(`ddd` highlights the complete `ddd`, not unrelated individual `d`s), while
preserving the original spelling and Unicode text elements. Whitespace-separated
words are highlighted independently. Only complete, compact, ordered fuzzy matches
of at least three characters are shown; repeated-letter queries do not use fuzzy
highlighting. Highlighting is display-only and does not affect ranking or recall.

Everything expressions (filters, operators, quotes, wildcards and grouping) are
conservatively left unhighlighted rather than interpreting syntax as matched text;
plain drive-qualified paths are supported. Pinyin initials, aliases and accent
normalization can retrieve results without literal characters in the displayed
text, so those transformations do not manufacture highlights. Theme accent color
and semibold weight follow live theme changes. The existing detail location is a
read-only TextBlock; its accessible text and the **复制路径** action are retained.

## Keyboard

- `Alt+Space`: show or hide Luma (configurable)
- `Up` / `Down`: select a result
- `Enter`: open
- `Ctrl+Enter`: reveal in File Explorer
- `Ctrl+Shift+Enter`: run as administrator
- `Ctrl+C`: normal text copy while editing the search box; copy the selected path
  when focus is in the result list (path copying is also available in actions)
- `PageUp` / `PageDown`: open the full-results view or move by one result page
- `Ctrl+G`: switch an Open/Save dialog to the selected folder
- `Right`: caret movement in the search box, actions when focus is in results
- `Ctrl+O`: actions
- `Ctrl+,`: settings
- `Escape`: leave the full-results view, then hide

Drag the launcher from the search icon, shortcut badge, or other empty chrome.

`Luma.exe --settings` opens the settings window directly.

## Personalization formats

Settings accepts one application alias per line:

```text
vsc=Visual Studio Code
wx=微信
```

Custom commands use `keyword|title|executable|arguments|working directory`.
`{query}` is replaced with text following the keyword:

```text
note|新建记事|notepad.exe|{query}|
code|用 VS Code 打开|code.cmd|{query}|%USERPROFILE%
```
