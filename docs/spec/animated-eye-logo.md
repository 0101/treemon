# Animated Eye Logo

## Goals

- Replace the plain-text H1 title with an inline SVG eye that visually connects to the app's icon
- Animate the pupil to look in a random direction on each data update, creating a "scanning" effect
- Remove the "Treemon" and ": FolderName" text from the header entirely (the `<title>` tag covers the app name)

## Expected Behavior

- The H1 contains only the animated SVG eye graphic — no text
- The eye has an almond-shaped outline, a translucent iris circle, and a solid pupil
- On each `DataLoaded` message (~1s), the pupil shifts to a random offset (±1.5px horizontal, ±1.0px vertical)
- The shift animates smoothly via CSS transition (~300ms ease)
- The eye uses teal (`#94e2d5`) to match the existing icon color palette
- Header controls (Sort, Compact buttons) remain positioned to the right

## Technical Approach

- **Inline SVG via Feliz**: Build the eye entirely in `App.fs` using Feliz SVG helpers — no external files
- **Model state**: Add `EyeDirection: float * float` to the Elmish Model, randomized in the `DataLoaded` handler
- **SVG structure**: Almond path outline + iris circle (translucent) + pupil circle (solid, offset by model state)
- **CSS transition**: `.eye-pupil { transition: cx 0.3s ease, cy 0.3s ease; }` for smooth movement
- **Header cleanup**: Remove "Treemon" text and `.folder-accent` span from H1; remove unused `.folder-accent` CSS

## Key Files

- `src/Client/App.fs` — Model, update (randomize on DataLoaded), view (viewEyeLogo + header)
- `src/Client/index.html` — CSS for `.eye-logo` sizing, `.eye-pupil` transition; remove `.folder-accent`
