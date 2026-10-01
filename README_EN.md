# CalcPaper · Calculation Scratchpad
[**简体中文**](README.md) | **English**

> A Windows calculator that works like ruled paper — one expression per line, results as you type, and press Enter to carry the result into the next line.

![Platform](https://img.shields.io/badge/platform-Windows%20x64-0078D6)
![.NET](https://img.shields.io/badge/.NET-8.0-512BD4)
![WebView2](https://img.shields.io/badge/WebView2-Evergreen-0C59A4)
![License](https://img.shields.io/badge/license-MIT-green)

|                 Main window                  |                Operator menu (drawer)                 |
| :------------------------------------------: | :---------------------------------------------------: |
| ![Main window](docs/screenshots/01-main.png) |  ![Operator menu](docs/screenshots/02-menu.png) |
|   ![Settings](docs/screenshots/03-settings.png)   |  ![Symbol sorting](docs/screenshots/04-sort.png)  |

## Contents

1. [Introduction](#1-introduction)
2. [Key Features](#2-key-features)
3. [Basic Features](#3-basic-features)
4. [How to Use](#4-how-to-use)
5. [Supported Operators, Functions and Constants](#5-supported-operators-functions-and-constants)
6. [Operator Precedence](#6-operator-precedence)
7. [Number Formatting and Error Messages](#7-number-formatting-and-error-messages)
8. [Tech Stack and Requirements](#8-tech-stack-and-requirements)
9. [Two Build Variants and When to Use Them](#9-two-build-variants-and-when-to-use-them)
10. [Download and Install](#10-download-and-install)
11. [Build from Source](#11-build-from-source)
12. [Directory Structure](#12-directory-structure)
13. [Data and Privacy](#13-data-and-privacy)
14. [FAQ](#14-faq)
15. [Known Limitations](#15-known-limitations)
16. [Version and Changelog](#16-version-and-changelog)
17. [Contributing and Feedback](#17-contributing-and-feedback)
18. [Acknowledgments](#18-acknowledgments)
19. [License](#19-license)

## 1. Introduction

"CalcPaper" is a Windows desktop calculator whose interface is a **sheet of ruled paper**: every line holds one expression and shows its result **in real time**; press `Enter` to evaluate the current line and **carry the result down** to the next line — as natural as chaining derivations on paper.

It is **portable and installation-free** (unzip and run) and **fully offline** (no network requests, no telemetry, no account). All data stays in the `data\` folder next to the program; nothing is written to `%APPDATA%` or the registry (except the optional "run at startup" entry).

## 2. Key Features

- **Instant per-line evaluation** — each line is an independent expression, evaluated as you type; long expressions wrap automatically with adaptive row height and no inner scrollbars.

- **Result carry-over** — `Enter` fills the result of the current line into the next line (copied as a plain number, without thousands separators), which naturally supports multi-step calculations.

- **Operator-menu drawer** — the menu button at the bottom right expands 23 operator/function bubbles; one click inserts at the **cursor position**, and function bubbles automatically place the cursor inside the parentheses.

- **Reorderable symbols** — right-click inside the drawer → "Symbol sorting": drag bubbles to reorder within a row, drag the row handle to reorder whole rows, with "Reset / Save", and the order is persisted.

- **Line notes** — any line can carry a note; click the pencil icon to type it, and it collapses into a gray pill.

- **Double-click to copy** — double-click a result to copy the plain number, with a "Copied" tooltip at the mouse.

- **Desktop-friendly** — always on top, close to tray, summon by global hotkey, silent auto-start, single-instance.

- **Lenient input** — full-width and alias symbols such as `× ÷ （） ＋ － ＾ π √ ∛`, the letter `x` as multiplication, and any whitespace are all accepted directly.

- **Dependency-free front end** — the UI and the calculation engine are native HTML/CSS/JavaScript (ES5 style, no framework, no third-party library), and the engine has 127 self-test cases.

## 3. Basic Features

The interface has three areas: the **title bar** (always on top / minimize / close to tray), the **workspace** (ruled paper + operator-menu drawer), and the **bottom bar** (current document name + autosave status + action buttons).

| Feature              | Description                                                                       |
| -------------------- | --------------------------------------------------------------------------------- |
| Ruled-paper input    | One expression per line, evaluated live and shown after `=` on the right          |
| Result carry-over    | `Enter` evaluates the current line and seeds the next line with the value         |
| Line notes           | Each line can hold one note that does not participate in calculation              |
| Copy result          | Double-click a result to copy the plain number                                    |
| Operator menu        | 23 symbol bubbles; click to insert at the cursor of the current line              |
| Symbol sorting       | Drag to reorder symbols and group rows; resettable to default                     |
| Document management  | Autosave, save as, load, new, delete (to the Recycle Bin)                         |
| Status hints         | The bottom bar shows the current file name and "Autosaved HH:mm"; turns red on error |
| Settings             | Trigonometry in degrees/radians, thousands separators, run at startup, global hotkey |
| Desktop integration  | Always on top, hide to tray, summon by global hotkey, single instance, silent auto-start, splash animation |

**The 23 bubbles in the operator menu** (default order, 7 rows):

| Group             | Bubbles                        |
| ----------------- | ------------------------------ |
| Power & roots     | `x²` `x³` `^` `√` `³√`         |
| Trigonometry      | `sin` `cos` `tan`              |
| Inverse trig      | `asin` `acos` `atan`           |
| Logarithms        | `log` `ln`                     |
| Constants         | `π` `e`                        |
| Operators         | `%` `!` `mod` `rem`            |
| General functions | `abs` `round` `floor` `ceil`   |

## 4. How to Use

### Quick reference

| Action                        | Entry point / Key                                              | Description                                                                                        |
| ----------------------------- | ------------------------------------------------------------- | -------------------------------------------------------------------------------------------------- |
| Enter an expression           | Click any line and type                                       | Values update as you type; the current line is lightly highlighted and shows the "expression" placeholder |
| Evaluate and carry down       | `Enter`                                                       | The result fills the next line; does nothing on an empty line or on error, and never creates a line |
| Move between lines            | `↑` / `↓`                                                     | Jump to the previous / next line, with the cursor at the end of the text                           |
| Add / edit a note             | Pencil icon in the row → input box / click the gray pill      | `Enter` saves, `Esc` cancels                                                                       |
| Copy a result                 | Double-click the result area                                  | Copies the plain number (no separators) and shows a "Copied" tooltip                               |
| Expand / collapse the menu    | The three-bar button at the far right of the bottom bar       | Toggles on click; the drawer is 280 px wide and **pushes** the paper aside instead of covering it  |
| Insert a symbol               | Click a bubble in the drawer                                  | Inserts at the cursor; function bubbles place the cursor inside the parentheses                    |
| Reorder symbols               | Right-click in the drawer → "Symbol sorting"                  | Drag bubbles within a row; drag the row handle or the empty space at the row end to move whole rows; "Reset / Save" at the bottom |
| Open settings                 | The gear button in the bottom bar                             | Collapses with `Esc` or by clicking outside the panel                                              |
| Save document as              | "Save as" in the bottom bar                                   | Saves as `*.json`, default file name `计算稿纸.json`                                               |
| Load document                 | "Load" in the bottom bar                                      | Replaces all current content and focuses the last line                                             |
| New document                  | "New" in the bottom bar                                       | If unsaved, an in-app confirmation appears that can "Save as" directly; confirming returns to a "temporary document" |
| Delete document               | "Delete" in the bottom bar                                    | Available only once bound to a file; the file goes to the Recycle Bin (recoverable) and the UI returns to a temporary document |
| Toggle always on top          | The pin button in the title bar                               | Toggles always-on-top; the state is synced by a window acknowledgment                              |
| Minimize                      | `—` in the title bar                                          | Regular minimize                                                                                   |
| Close to tray                 | `×` in the title bar                                          | **Does not exit the program**; the icon stays in the tray                                          |
| Show the window again         | Left-click the tray icon                                      | Or use the global hotkey                                                                           |
| Fully exit                    | Right-click the tray icon → "Exit completely"                 | Flushes to disk before exiting                                                                     |
| Summon the window             | Global hotkey (default `Alt+C`), or a recorded "double-tap key" in settings | `Alt` / `Ctrl` / `Shift` / `Win`, `F1`–`F12`, `CapsLock`, and `Tab` can all be double-tap keys |
| Minimize to taskbar           | `Alt+F4`                                                      | Captured and minimized instead of exiting                                                          |
| Resize the window             | Drag the **right edge / bottom edge / bottom-right corner**   | Deliberately limited to these three: dragging the top edge or top-left corner moves the window origin and makes the content jitter, so they are disabled |

### Three quick tips

1. **Chained calculation**: type `1000*1.13` on line 1 and press `Enter`; line 2 automatically starts with `1130`, where you can continue with `-250`.
2. **Drawer insertion**: place the cursor where you need it, then click a bubble; `sin`, `log`, and the like insert `sin()` with the cursor between the parentheses, so you can type the argument right away.
3. **Notes do not participate in calculation**: a note is just explanatory text attached under a line; only rows with both an empty expression and an empty note are treated as blank and recycled.

## 5. Supported Operators, Functions and Constants

### Operators

| Symbol  | Meaning                                                            | Sample input    | Result     |
| ------- | ------------------------------------------------------------------ | --------------- | ---------- |
| `+` `-` | Addition, subtraction (also unary sign)                            | `12-3.5`        | 8.5        |
| `*`     | Multiplication (aliases `×` `✕` `✖` `x` `X` `＊`)                  | `99x878.467`    | 86,968.233 |
| `/`     | Division (aliases `÷` `／`)                                        | `5875/5`        | 1,175      |
| `^`     | Exponentiation, right-associative                                 | `(1+2)*(3+4)^2` | 147        |
| `%`     | Between two numbers = modulo; after a number = percent             | `10%3` / `50%`  | 1 / 0.5    |
| `!`     | Postfix factorial (non-negative integers only, up to 170)          | `5!`            | 120        |
| `(` `)` | Grouping, nestable                                                 | `(1+2)*3`       | 9          |
| `,`     | Function argument separator                                        | `log(2,8)`      | 3          |

### Functions (all require parentheses)

| Function                      | Description                                                     | Sample input                | Result            |
| ----------------------------- | --------------------------------------------------------------- | --------------------------- | ----------------- |
| `sqrt(x)` / `√(x)`            | Square root (reports "Cannot compute" for negative numbers)     | `sqrt(16)` / `√(2)`         | 4 / 1.41421356237 |
| `cbrt(x)` / `∛(x)`            | Cube root, **supports negative numbers**                        | `cbrt(-8)`                  | -2                |
| `sin(x)` `cos(x)` `tan(x)`    | Sine / cosine / tangent, using the configured **degree or radian mode** | `sin(30)` (degrees)         | 0.5               |
| `asin(x)` `acos(x)` `atan(x)` | Inverse sine / cosine / tangent; the result follows the current angle mode | `asin(0.5)` (degrees)       | 30                |
| `arcsin` `arccos` `arctan`    | Aliases of the row above                                        | `arctan(1)` (degrees)       | 45                |
| `ln(x)`                       | Natural logarithm                                               | `ln(e)`                     | 1                 |
| `log(x)`                      | Common logarithm (base 10)                                      | `log(100)`                  | 2                 |
| `log(b,x)`                    | Logarithm with a specified base                                 | `log(2,8)`                  | 3                 |
| `abs(x)`                      | Absolute value                                                  | `abs(-3.5)`                 | 3.5               |
| `round(x)`                    | Round half away from zero                                       | `round(-2.5)`               | -3                |
| `floor(x)` `ceil(x)`          | Round down / up                                                 | `floor(-2.1)` / `ceil(2.1)` | -3 / 3            |
| `mod(a,b)`                    | Modulo; the result takes the sign of the **divisor**            | `mod(-7,3)`                 | 2                 |
| `rem(a,b)`                    | Remainder; the result takes the sign of the **dividend**        | `rem(-7,3)`                 | -1                |

### Numeric constants

| Constant   | Meaning        | Sample input  | Result                        |
| ---------- | -------------- | ------------- | ----------------------------- |
| `pi` / `π` | Pi             | `pi` / `2*π`  | 3.14159265359 / 6.28318530718 |
| `e`        | Euler's number | `e` / `ln(e)` | 2.71828182846 / 1             |

### Lenient input (paste any of these forms)

| You type                                             | The engine reads it as |
| ---------------------------------------------------- | ---------------------- |
| `×` `✕` `✖` `x` `X` `＊`                              | multiplication `*`     |
| `÷` `／`                                              | division `/`           |
| `（` `）` `＋` `－` `＾`                                | `(` `)` `+` `-` `^`    |
| `−` (Unicode minus sign U+2212)                       | `-`                    |
| `π`                                                   | the constant `pi`      |
| `√(x)`                                                | the function `sqrt(x)` |
| `∛(x)`                                                | the function `cbrt(x)` |
| Half-width space, full-width space, Tab, no-break space, etc. | ignored entirely |

### Not supported

Variables and assignment, equation solving, matrices / determinants, complex numbers, statistical functions, unit conversion, summation `Σ` / integration, user-defined functions, and a dedicated n-th-root symbol (use `x^(1/n)` instead).

## 6. Operator Precedence

Listed from **lowest to highest**:

| Precedence   | Operator                                | Associativity | Notes                                                    |
| :----------: | --------------------------------------- | ------------- | -------------------------------------------------------- |
| 1 (lowest)   | `+` `-`                                 | left          | Binary addition and subtraction                          |
| 2            | `*` `/` `%`                             | left          | Multiplication, division, and modulo between two numbers |
| 3            | unary `+` `-`                           | right         | Sign                                                     |
| 4            | `^`                                     | **right**     | Exponentiation; consecutive occurrences evaluate right to left |
| 5            | postfix `!` `%`                         | left          | Factorial and percent; can be chained                    |
| 6 (highest)  | functions, parentheses, constants, numbers | —          | Grouping and value lookup                                |

### Precedence examples

| Expression | Result | Why                                                          |
| ---------- | ------ | ------------------------------------------------------------ |
| `1+2*3`    | 7      | Multiplication before addition                               |
| `(1+2)*3`  | 9      | Parentheses first                                            |
| `2+3*4`    | 14     | Multiplication before addition                               |
| `-2^2`     | -4     | Exponentiation before unary minus, i.e. `-(2^2)`             |
| `2^-1`     | 0.5    | The exponent may carry a sign                                |
| `2^3^2`    | 512    | `^` is right-associative, i.e. `2^(3^2)`                     |
| `2*3!`     | 12     | Factorial before multiplication                              |
| `2^3!`     | 64     | Factorial binds tighter than exponentiation, i.e. `2^(3!)`   |
| `5!^2`     | 14,400 | i.e. `(5!)^2`                                                |
| `-3!`      | -6     | i.e. `-(3!)`                                                 |
| `log(2,8)` | 3      | Functions and parentheses evaluate first                     |
| `10%3`     | 1      | `%` followed by a number → modulo                            |
| `10%`      | 0.1    | `%` after a number → percent                                 |
| `50%*2`    | 1      | Evaluate 0.5 first, then multiply by 2                       |
| `7%-3`     | -2.93  | Evaluate `7%` = 0.07 first, then subtract 3                  |
| `200+10%`  | 200.1  | Percent means "divide by 100", **not** "add 10%"            |

### The dual role of `%`

The same `%` sign takes its meaning from **what is on its right**:

- Immediately followed by a **number, a constant (`π`/`pi`/`e`), or a left parenthesis** → there are operands on both sides → **modulo**: `10%3` = 1, `(10%)%3` = 0.1

- Otherwise (followed by an operator, a right parenthesis, a comma, a function name, or the end of the expression) → **postfix percent**: `10%` = 0.1, `50%*2` = 1, `10%+3` = 3.1

### Two common pitfalls

- **Functions require parentheses**: `sin30`, `log 100`, and `mod(1)` all report "Expression error"; the correct forms are `sin(30)`, `log(100)`, and `mod(2,8)`.

- **`√` and `∛` are also functions**: write `√(16)` and `∛(-8)`; `√16` is not allowed.

## 7. Number Formatting and Error Messages

### Result display rules

| Rule                                                             | Sample input           | Displayed result               |
| ---------------------------------------------------------------- | ---------------------- | ------------------------------ |
| Thousands separators (on by default; can be turned off in settings) | `1000000`              | 1,000,000 (`1000000` when off) |
| Absolute value ≥ 1e15 uses scientific notation                   | `10^21`                | 1e+21                          |
| Below the threshold, plain notation with grouping                | `10^14`                | 100,000,000,000,000            |
| Non-zero and absolute value < 1e-9 uses scientific notation      | `10^-10`               | 1e-10                          |
| Between 1e-9 and 1e-6, expands to a decimal                      | `10^-7`                | 0.0000001                      |
| Rounded to 12 significant digits, removing floating-point tails  | `0.1+0.2`              | 0.3                            |
| Trailing zeros dropped; negative zero shown as 0                 | `4.500` / `-0`         | 4.5 / 0                        |
| Floating-point residue at exact multiples of 90° is snapped to 0 | `cos(90)` / `sin(180)` | 0 / 0                          |

> Calculations are based on IEEE 754 double-precision floating point, and results are uniformly rounded to 12 significant digits; for example, `170!` is shown as `7.25741561531e+306` rather than a long string of digits.

### Error messages

| Situation                                                                   | Sample input                           | UI message                                                    |
| --------------------------------------------------------------------------- | -------------------------------------- | ------------------------------------------------------------- |
| Empty line                                                                  | (empty)                                | Shows only `=`                                               |
| Incomplete syntax / illegal character / unknown name / wrong argument count | `55+`, `(1+2`, `abc`, `sin30`          | Expression error                                             |
| Divisor (including modulo) is 0                                             | `1/0`, `10%0`, `mod(5,0)`              | Divisor cannot be 0                                          |
| Mathematically undefined or overflow                                        | `sqrt(-1)`, `tan(90)` (degrees), `10^9999` | Cannot compute                                            |
| Factorial input is not a non-negative integer                               | `2.5!`, `(-3)!`                        | Factorial only supports non-negative integers                |
| Factorial result out of range (> 170)                                       | `171!`                                 | Result too large, out of range                               |
| Inverse-trig input outside `[-1, 1]`                                        | `asin(2)`                              | The input to asin/acos must be between -1 and 1              |
| Logarithm argument ≤ 0                                                      | `log(0)`, `ln(-1)`                     | The logarithm argument must be greater than 0                |
| Logarithm base ≤ 0 or = 1                                                   | `log(1,8)`, `log(-2,8)`                | The logarithm base must be greater than 0 and not equal to 1 |

## 8. Tech Stack and Requirements

| Layer                | Choice                                                                                      |
| -------------------- | ------------------------------------------------------------------------------------------- |
| Host app             | C# / .NET 8 (`net8.0-windows`) + WinForms                                                   |
| UI                   | Native HTML / CSS / JavaScript (ES5 style, zero dependencies, no ES Modules, loaded over `file://`) |
| Rendering engine     | Microsoft.Web.WebView2 `1.0.4191.47` (Edge Evergreen)                                       |
| Calculation engine   | Hand-written recursive-descent parser: lexing → parsing (evaluate while parsing), rounded to 12 significant digits |
| System integration   | Win32 P/Invoke: `RegisterHotKey` global hotkey, `WH_KEYBOARD_LL` low-level keyboard hook, COM jump list |
| Persistence          | JSON (`settings.json`, `autosave.json`) + a text log `app.log`                              |

### Requirements

- **OS**: Windows 10 / 11 (x64)

- **WebView2 Runtime**: **required** (Evergreen; usually already installed with Microsoft Edge on Win10/11)

- **.NET 8 Desktop Runtime**: **required**

## 9. Two Build Variants and When to Use Them

Both variants are **portable** (installation-free; unzip and run). The only difference is **whether the .NET 8 runtime is bundled**:

| Aspect                              | `…WebView2-.NET8(exclude).zip`                                                                                                                                                                                                        | `…WebView2-.NET8(include).zip`                              |
| ----------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ | ----------------------------------------------------------- |
| Distribution                        | Framework-dependent                                                                                                                                                                                                                   | Self-contained                                              |
| Size after extraction               | ≈ 2.65 MB (26 files)                                                                                                                                                                                                                  | ≈ 162 MB (485 files)                                        |
| Zip size                            | ≈ 0.9 MB                                                                                                                                                                                                                              | ≈ 65 MB                                                     |
| Requires .NET 8 Desktop Runtime     | **Yes** (if the target machine does **not** have .NET 8 Desktop Runtime, the program shows a **dialog on startup saying the runtime is missing**; the dialog includes an official **download link that opens in your browser** to install it, and running it again then works) | No (bundled)                                                |
| Requires WebView2 Runtime           | Yes                                                                                                                                                                                                                                   | Yes                                                         |
| Best for                            | Machines that already have (or can install) .NET 8, where size and distribution speed matter; or centralized intranet deployment                                                                                                        | Environments of unknown setup that need an offline, "unzip and run" all-in-one package |

## 10. Download and Install

1. Download the zip for the variant you want from [Releases](https://github.com/xu-liyan/CalcPaper/releases).
2. **Extract to a writable folder** (for example `D:\CalcPaper`).

   > The program writes settings, autosave data, logs, and WebView2 user data to the `data\` folder next to the exe. Placing it in a protected location such as `C:\Program Files` fails with a "not writable" message and exits.
3. Double-click `CalcPaper.exe`.

A short splash animation plays on startup. **Closing the window does not exit the program** — the icon stays in the tray, and you can bring it back at any time via the tray or the global hotkey (default `Alt+C`).

## 11. Build from Source

You need the .NET 8 SDK and Windows:

```powershell
dotnet build
dotnet run
```

Publishing the two variants (the output folder names match the existing release artifacts):

```powershell
# Framework-dependent (no .NET runtime bundled)
dotnet publish -c Release -r win-x64 --self-contained false -o "publish\CalcPaper-portable-win-x64-WebView2-.NET8(exclude)"

# Self-contained (with the .NET runtime bundled)
dotnet publish -c Release -r win-x64 --self-contained true -o "publish\CalcPaper-portable-win-x64-WebView2-.NET8(include)"
```

**Engine self-test**: open [`wwwroot/tests.html`](wwwroot/tests.html) directly in a browser; all 127 cases are compared against expected values and summarized as "passed x / 127". Run this page first after changing the calculation engine.

## 12. Directory Structure

```
Calculation_paper/
├─ src/                     C# host program
│  ├─ Program.cs            Entry point + main window (window behavior, tray, clipboard, message dispatch)
│  ├─ Bridge.cs             JS ↔ C# message bridge (protocol documented in the file header)
│  ├─ PaperIo.cs            Pure IO / validation for documents and settings (UI-independent, independently testable)
│  ├─ TrayMenu.cs           Tray icon and context menu
│  ├─ GlobalHotkey.cs       RegisterHotKey global hotkey
│  ├─ DoubleAltWatcher.cs   Low-level keyboard hook (Alt+F4 to minimize, double-tap key to summon)
│  ├─ ShellIntegration.cs   Single instance, cross-instance command broadcast, taskbar jump list
│  ├─ SplashWindow.cs       Splash animation
│  ├─ AppPaths.cs           Data directory and one-time migration of legacy data
│  └─ AutoStart.cs          Run at startup (registry Run key)
├─ wwwroot/                 UI and calculation engine (shipped with the program)
│  ├─ index.html / style.css   UI structure and styles (can also be opened directly in a browser to preview the design)
│  ├─ calc-engine.js        Expression engine (zero dependencies; can be included standalone)
│  ├─ app.js                Ruled-row model, keyboard interaction, notes, copying
│  ├─ menu.js               Operator-menu drawer and drag-to-sort symbols
│  ├─ settings.js           Settings overlay
│  ├─ persist.js            Autosave and document file operation scheduling
│  ├─ bridge.js / tooltip.js / confirm.js
│  └─ tests.html            Engine self-test page (127 cases)
├─ assets/app.ico           exe icon
├─ docs/screenshots/        README images
├─ CalcPaper.csproj
├─ LICENSE
├─ README.md
├─ README_EN.md
└─ .gitignore
```

After running, a `data/` folder is created next to the exe (see the next section).

## 13. Data and Privacy

- **Fully offline**: the program makes no network requests and contains no telemetry, accounts, or auto-update.

- **Data location**: `<program folder>\data\`

| File            | Contents                                                                                       |
| --------------- | ---------------------------------------------------------------------------------------------- |
| `settings.json` | Degree/radian mode, thousands separators, global hotkey, operator-menu order, path of the last opened document |
| `autosave.json` | Autosave target when no file is bound (the "temporary document")                               |
| `app.log`       | Runtime log (startup timing, fallback records of exceptions)                                   |
| `WebView2\`     | User-data directory of the rendering engine                                                    |

- **Nothing is written to `%APPDATA%`**: only on the first run, when no local data file exists yet, it tries once to copy legacy settings from the old `%APPDATA%\CalculationPad\` (the old files are kept and not deleted).

- **Uninstall**: just delete the whole program folder; deleting a document sends it to the system Recycle Bin, from which it can be restored.

- **When data is saved**: content changes are autosaved with a **700 ms debounce**; the app flushes to disk before exiting.

## 14. FAQ

**Q: It says "the current program folder is not writable". What should I do?**
A: Move the whole program folder to a writable location (such as the D drive) and run it again. The program deliberately does not fall back to `%APPDATA%`, to keep the "portable" promise.

**Q: Double-clicking the exe does nothing, or the UI is blank.**
A: It is most likely a missing WebView2 Runtime. Win10/11 usually ships it with Edge; if it is missing, install the Microsoft Edge WebView2 Evergreen Runtime.

**Q: Why do `sin30` and `log 100` report "Expression error"?**
A: Functions must be written with parentheses: `sin(30)`, `log(100)`. Omitting the parentheses makes them unknown identifiers.

**Q: How do I enter a cube root (or an n-th root)?**
A: Use `cbrt(8)` or `∛(8)` to get 2; `8^(1/3)` also works. For n-th roots, use `x^(1/n)` uniformly, for example the fourth root `16^(1/4)`.

**Q: Do `8^1/3` and `8^(1/3)` give the same result?**
A: No. `^` has higher precedence than `/`, so `8^1/3` = (8¹)÷3 ≈ 2.66666666667; always parenthesize roots.

**Q: Is `%` a percent or a modulo?**
A: It depends on what is on its right: immediately followed by a number, a constant, or a left parenthesis it is a modulo (`10%3` = 1); otherwise it is a postfix percent (`10%` = 0.1).

**Q: Why is `200+10%` equal to 200.1 and not 220?**
A: Here `%` is the mathematical "divide by 100", i.e. 200 + 0.1. This tool does not provide business semantics such as "add 10%".

**Q: After setting a global hotkey nothing happens, and the panel shows red text.**
A: The combination is already taken by another program; choose a different one. Clicking "Reset" returns to the default `Alt+C`. Also, some security software blocks keyboard hooks, which disables "double-tap a key to summon".

**Q: I clicked close — where did the program go?**
A: Closing hides it to the tray; it has not exited. Left-click the tray icon to show it again; right-click → "Exit completely" is the real exit.

**Q: Why can the window only be resized from the right, bottom, and bottom-right?**
A: By design. Dragging the top edge or the top-left corner moves the window origin and makes the content jitter, so only the three well-behaved edges/corners are enabled.

**Q: What format is the document file?**
A: JSON, structured as `{ "version": 1, "rows": [ { "expr": "expression", "note": "note" } ] }`, with the `.json` extension and the default file name `计算稿纸.json`.

**Q: Is there a Mac / Linux version?**
A: No. The program depends on WinForms, WebView2, and several Win32 APIs, so it currently supports Windows x64 only.

## 15. Known Limitations

- It only does expression evaluation — no variables, assignment, equation solving, matrices, complex numbers, statistics, unit conversion, summation / integration, or user-defined functions.

- Trigonometry covers only `sin` / `cos` / `tan` and their inverses; there are no hyperbolic functions or multi-argument `atan2`.

- Mathematically undefined cases (`sqrt(-1)`, `tan(90°)`, etc.) uniformly report "Cannot compute" instead of returning a complex number or infinity.

- Factorials are capped at 170; exceeding it reports "Result too large, out of range".

- Based on IEEE 754 double-precision floating point, results are shown to 12 significant digits; it is not suitable for very high-precision scenarios.

- Window resizing is limited to the right edge, the bottom edge, and the bottom-right corner (see the FAQ).

- Features that rely on the Win32 keyboard hook may be blocked by some security software.

- Windows x64 only.

## 16. Version and Changelog

**v1.5.0** (current)

- Calculation: the four arithmetic operations, powers and parentheses, square/cube roots, factorial, percent and modulo, logarithms (base 10 / custom base / natural), trigonometry and inverse trigonometry (degrees/radians), rounding and absolute value, plus the `π` / `e` constants.

- Interaction: operator-menu drawer (23 symbol bubbles) with drag-to-sort, per-line result carry-over, line notes, double-click to copy results.

- Desktop integration: always on top, hide to tray, single instance, global hotkey (including "double-tap a key"), silent auto-start, `Alt+F4` to minimize.

- Document management: 700 ms debounced autosave, save as / load / new / delete (Recycle Bin), status-bar hints.

Historical releases are recorded in [Releases](https://github.com/xu-liyan/CalcPaper/releases).

## 17. Contributing and Feedback

- **Bug reports**: feel free to open an [Issue](https://github.com/xu-liyan/CalcPaper/issues); please include your Windows version, the variant you use, and a reproducible input expression together with the UI message.

- **Code contributions**: please keep the existing conventions — zero-dependency front end, ES5 style (only `var` and `function`), no ES Modules.

- **After changing the calculation engine**, first make all cases in [`wwwroot/tests.html`](wwwroot/tests.html) pass, and add new cases when necessary.

## 18. Acknowledgments

- The idea for this project comes from the [utools Calculation Scratchpad plugin](https://www.u-tools.cn/plugins/detail/%E8%AE%A1%E7%AE%97%E7%A8%BF%E7%BA%B8/). I really like that plugin's functionality, but it has no manually saved scratchpads, so they are often lost the next time it is opened; I also had some of my own requirements for various details, so I wrote one myself. The utools platform has a great many plugins — it is a real treasure box — so give it a try if you are interested.

- All code in this project was generated by DeepSeek-V4.1-Flash.

## 19. License

This project is open-sourced under the [MIT License](LICENSE), Copyright © 2026 xu-liyan.
