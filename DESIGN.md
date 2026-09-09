# tgrep-gui design

<!-- Hallmark pre-emit critique: Philosophy 4, Hierarchy 4, Execution 4, Specificity 4, Restraint 5, Variety 3. -->

## Direction

Minimal, modern Windows developer utility. Search and reading matches are the
primary activities. Keep the native Fluent control language and existing navigation.

## Layout

Use a compact heading, project path, prominent search field, then search options.
File filters expand inline from a native toggle. The results occupy the remaining
height: a resizable file list with a stable initial width and a flexible preview.
Only results need bordered surfaces. Avoid decorative cards around form fields.

Show the file name and its directory separately. Keep counts quiet and aligned.
Preview actions belong beside the selected path; keyboard shortcuts remain available.
Settings retains the existing grouped form and shares the platform resources.

## Tokens and interaction

- Typography: native UI font; existing MainViewModel font-size properties respect
  the user's interface scale. Code remains monospaced.
- Surfaces: SolidBackgroundFillColorBaseBrush, LayerFillColorDefaultBrush.
- Text: TextFillColorPrimaryBrush and TextFillColorSecondaryBrush.
- Borders: CardStrokeColorDefaultBrush; existing Card style for result panes.
- Accent: system accent, reserved for primary actions, selection and focus.
- Spacing: 4, 8, 12, 16, 24 logical pixels.
- Interaction: native hover, pressed, disabled and keyboard focus states. No added
  decorative motion. Preserve ListView virtualization and existing async loading.

Light, dark and system themes use platform resources. Do not replace native theme
tokens with a web palette. Test expanded filters, empty/populated results and narrow
windows when changing layout.
