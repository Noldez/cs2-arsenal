# Panorama CSS reference (CS2)

Derived from `game/bin/linuxsteamrt64/libpanorama.so` — the property vocabulary from
`CStylePropertyFactory` registration plus the doc strings Valve compiles in (the same text
`dump_panorama_css_properties` prints). **140 properties registered.** The factory index is a
byte, so the vocabulary is hard-capped at 255.

This is Panorama CSS, **not** web CSS. Assuming web behaviour is the single biggest source of
silently-broken stylesheets, because **the resourcecompiler does not validate CSS** — bogus
properties compile clean and only fail at runtime (check `console.log` for `[Panorama] *****
Parsing error`).

## Not present (checked by name against the binary)

`display`, flex/grid and every companion (`justify-content`, `align-items`, `gap`, `order`,
`flex-*`), `float`, `clear`, `top`/`right`/`bottom`/`left`, `box-sizing`, `content`,
`list-style`, `outline`, `filter`, `mix-blend-mode` (it is `-s2-mix-blend-mode`), `object-fit`,
`aspect-ratio`, `clip-path`, `mask`, `pointer-events`, `user-select`, `calc()`,
`var()`/custom properties, `rgb()`/`rgba()`/`hsl()`, and `@media`.

Layout uses Panorama's own model instead: `flow-children`, `width`/`height` taking
`fit-children | fill-parent-flow(w) | width-percentage(p)`, plus `x`/`y`/`z`, `align`,
`ignore-parent-flow`.

## Selectors

```
.class    #id    PanelType    descendant (space)
```

Pseudo-classes: `:active :activationdisabled :descendantfocus :disabled :focus :hover :root :selected`
Structural: `:nth-child :first-child :last-child`

No `:not()`, no `::before`/`::after`, no `:nth-of-type`, no attribute selectors.

## At-rules

`@define`, `@import`, `@keyframes` — anything else errors with "Found unsupported CSS at-rule".

**`@keyframes` names MUST be single-quoted**, or the whole stylesheet fails to parse and every
panel in the layout silently fails to build:

```css
@keyframes 'GlowPulse' { 0% { opacity: 0.5; } 100% { opacity: 1; } }
.thing { animation-name: GlowPulse; }   /* unquoted here */
```

## Value vocabulary

- **colours**: `#rrggbb` / `#rrggbbaa`, and `gradient(...)`. No `rgb()`/`rgba()`.
- **gradients** (2008 WebKit form):
  `gradient( linear, 0% 0%, 0% 100%, from( #fff ), color-stop( 0.3, #eee ), to( #ccc ) )`
  `gradient( radial, 50% 50%, 0% 0%, 80% 80%, from( #0f0 ), to( #00f ) )`
- **timing**: `ease ease-in ease-out ease-in-out linear cubic-bezier`
- **transforms**: `rotate rotate3d rotatex rotatey rotatez scale scale3d scalex scaley scalez
  skew skewx skewy translate translate3d translatex translatey translatez`
- **blend modes**: `additive colorburn colordodge darken hardlight hue lighten multiply normal
  overlay screen softlight`

## Syntax gotchas that bite

| Property | Correct form | Common mistake |
|---|---|---|
| `@keyframes` | `@keyframes 'Name'` | unquoted name → **whole stylesheet dies** |
| `background-size` | `contains` | `contain` (web spelling) |
| `img-shadow` | `2px 2px 8px 3.0 #333333b0 alpha-only` — x, y, blur, **strength float**, colour | passing a spread length like `6px` |
| `text-shadow` | `2px 2px 8px 3.0 #333333b0` — x, y, blur, **strength float**, colour | passing a spread length |
| `box-shadow` | `#ffffff80 4px 4px 8px 0px` — **colour first**, then x, y, blur, spread | colour last (web order) |
| `blur` | `gaussian( 2.5 )` | `blur(2.5px)` |
| `url()` | only inside `background-image` / `border-image-source` | using it elsewhere |
| `visibility` | `visible` \| `collapse` | `hidden` |
| `overflow` | `squish` \| `clip` \| `scroll` \| `noclip`, per axis | `hidden`/`auto` |

`box-shadow` also accepts a leading shape keyword: `inset`, `fill`, or `hollow`.

## Layout model

- `width`/`height`: `fit-children` (default), `<px>`, `<%>`, `fill-parent-flow( <weight> )`,
  `width-percentage(<%>)` / `height-percentage(<%>)` for aspect ratios.
- `flow-children`: how children stack. **A container without it overlaps all its children at the
  same origin** — this is the classic "layout is fucked" bug.
- `position: 3% 20px 0px` — only valid when *not* in a flowing layout.
- `align`, `horizontal-align`, `vertical-align`, `ignore-parent-flow`, `layout-position: fixed`.
- `ui-scale: 150%` scales layout (fonts included), not just bitmaps.

## Full property list (140)

```
align, animation, animation-delay, animation-direction, animation-duration,
animation-fill-mode, animation-frame-time, animation-iteration-count, animation-name,
animation-timing-function, background-blur, background-color, background-color-opacity,
background-image, background-img-opacity, background-position, background-repeat,
background-size, background-texture-size, blur, border, border-bottom, border-bottom-color,
border-bottom-left-radius, border-bottom-right-radius, border-bottom-style,
border-bottom-width, border-brush, border-color, border-image, border-image-outset,
border-image-repeat, border-image-slice, border-image-source, border-image-width, border-left,
border-left-color, border-left-style, border-left-width, border-radius, border-right,
border-right-color, border-right-style, border-right-width, border-style, border-top,
border-top-color, border-top-left-radius, border-top-right-radius, border-top-style,
border-top-width, border-width, box-shadow, brightness, clip, color,
context-menu-arrow-position, context-menu-body-position, context-menu-position, contrast,
cursor, flow-children, font, font-family, font-size, font-stretch, font-style, font-weight,
height, horizontal-align, hue-rotation, ignore-parent-flow, img-shadow, layout-position,
letter-spacing, line-height, margin, margin-bottom, margin-left, margin-right, margin-top,
max-height, max-width, min-height, min-width, opacity, opacity-brush, opacity-mask,
opacity-mask-position, opacity-mask-scale, opacity-mask-threshold, overflow, padding,
padding-bottom, padding-left, padding-right, padding-top, paragraph-spacing, perspective,
perspective-origin, position, pre-transform-rotate2d, pre-transform-scale2d,
-s2-mix-blend-mode, saturation, sound, sound-out, text-align, text-decoration,
text-decoration-style, text-overflow, text-shadow, text-transform, texture-sampling,
tooltip-arrow-position, tooltip-body-position, tooltip-position, transform, transform-origin,
transition, transition-delay, transition-duration, transition-frame-time,
transition-high-framerate, transition-property, transition-timing-function, ui-scale,
ui-scale-x, ui-scale-y, ui-scale-z, vertical-align, visibility, wash-color, white-space, width,
world-blur, x, y, z, z-index
```

## Notable capabilities

- `clip: rect( 10%, 90%, 90%, 10% )` — also a radial mode (centre, start angle, angular width),
  animatable and cheap. Useful for wipe/reveal effects.
- `opacity-brush` / `opacity-mask` — gradient-driven fades.
- `background-blur`, `blur`, `world-blur` — the last blurs the game behind the panel.
- `brightness`, `contrast`, `saturation`, `hue-rotation`, `wash-color`.
- `sound` / `sound-out` — play a sound when a selector applies/unapplies.
- `perspective` + `perspective-origin` + 3D transforms — real depth for panels.
- `background-image` accepts **movies**: `url( "file://{movies}/Background1080p.webm" )`.

## Panel XML attributes (layout files)

`acceptsfocus acceptsinput always-cache-composition-layer analogstickscroll childfocusonhover
class clipaftertransform composition-layer-texture-name defaultfocus disabled
disallowedstyleflags draggable focusonhover focusonmousedown hittest hittestchildren id
inputnamespace keepscrolltobottom mousecanactivate never-cache-composition-layer onactivate
onblur oncancel oncontextmenu ondblclick ondescendantblur ondescendantfocus ondeselect onfocus
onload onmouseactivate onmousedown onmouseout onmouseover onmouseup onmovedown onmoveleft
onmoveright onmoveup onscrolledtobottom onscrolledtorightedge onselect ontabbackward
ontabforward overscroll-x overscroll-y readyfordisplay registerforreadyevents
rememberchildfocus require-composition-layer scrollparenttofitwhenfocused selectionpos
selectionposboundary sendchildscrolledintoviewevents style tabindex useglobalcontext`

**Note:** in a `custom_hud_layout` only a small subset is usable — Valve's `point_script.d.ts`
restricts it to `<Panel>`/`<Label>`/`<Image>`/`<Button>` with `id`, `class`, `hittest`, `text`,
`src`. The `on*` handlers require client-side scripting, which `custom_hud_layout` does not have.
