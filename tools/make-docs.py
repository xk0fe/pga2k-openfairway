"""Builds docs/index.html: the OpenFairway modding guide and API reference, one static page (it also works as a
GitHub Pages site from the docs folder, and opened straight from disk).

    python tools/make-docs.py

The guide's code comes from the example mods in examples/ (which are built, so the code on the page compiles),
and the reference is read from src/OpenFairway.API/Api.cs and its doc comments, so the page cannot drift from
the API. Run it again after changing either.
"""
import html
import json
import re
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent
API = REPO / 'src' / 'OpenFairway.API' / 'Api.cs'
OUT = REPO / 'docs' / 'index.html'
REPO_URL = 'https://github.com/xk0fe/pga2k25-openfairway'


# ---- syntax colouring -------------------------------------------------------------------------------------------

KEYWORDS = set('''public private internal protected sealed static readonly const class interface enum record struct namespace
using new return var if else for foreach in while do switch case default break continue true false null void string int long
float double bool byte uint ulong char object this base get set init event params out ref unsafe where unmanaged required
override virtual abstract is as typeof nameof await async try catch finally throw lock'''.split())
CS_TOKEN = re.compile(r'(//[^\n]*)|(\$?@?"(?:[^"\\\n]|\\.)*")|(\b0x[0-9A-Fa-f]+u?\b|\b\d+(?:\.\d+)?[fdmu]?\b)|([A-Za-z_]\w*)|(\s+)|(.)', re.S)


def csharp(code):
    out = []
    tokens = list(CS_TOKEN.finditer(code))
    for k, m in enumerate(tokens):
        comment, string, number, word, space, other = m.groups()
        text = html.escape(m.group(0))
        if comment:
            out.append(f'<span class="c">{text}</span>')
        elif string:
            out.append(f'<span class="s">{text}</span>')
        elif number:
            out.append(f'<span class="n">{text}</span>')
        elif word:
            following = next((t.group(0) for t in tokens[k + 1:] if not t.group(5)), '')
            if word in KEYWORDS:
                out.append(f'<span class="k">{text}</span>')
            elif following == '(':
                out.append(f'<span class="f">{text}</span>')
            elif word[0].isupper():
                out.append(f'<span class="t">{text}</span>')
            else:
                out.append(text)
        else:
            out.append(text)
    return ''.join(out)


def json_code(code):
    out = []
    for m in re.finditer(r'("(?:[^"\\]|\\.)*")(\s*:)?|([^"]+)', code):
        if m.group(1):
            cls = 'p' if m.group(2) else 's'
            out.append(f'<span class="{cls}">{html.escape(m.group(1))}</span>{html.escape(m.group(2) or "")}')
        else:
            out.append(html.escape(m.group(3)))
    return ''.join(out)


def block(code, lang='cs', title=None):
    code = code.strip('\n')
    body = csharp(code) if lang == 'cs' else json_code(code) if lang == 'json' else html.escape(code)
    head = f'<div class="code-title">{html.escape(title)}</div>' if title else ''
    return f'<div class="code">{head}<button class="copy" type="button">Copy</button><pre><code>{body}</code></pre></div>'


def example(name):
    path = REPO / 'examples' / name / f'{name}.cs'
    return block(path.read_text(encoding='utf-8'), 'cs', f'examples/{name}/{name}.cs')


# ---- the API, read from its source ------------------------------------------------------------------------------

def clean_doc(lines):
    text = ' '.join(l.strip().lstrip('/').strip() for l in lines)
    text = re.sub(r'</?summary>', '', text)
    text = re.sub(r'<see cref="([^"]+)"\s*/>', r'<code>\1</code>', text)
    text = re.sub(r'<paramref name="([^"]+)"\s*/>', r'<code>\1</code>', text)
    text = text.replace('&lt;', '<').replace('&gt;', '>')
    # The doc comments are written for code; keep their tags readable on the page.
    keep = re.sub(r'<(?!/?code>)', '&lt;', text)
    return keep.strip()


def read_api():
    types = []
    doc, current, depth, pending = [], None, 0, ''
    for raw in API.read_text(encoding='utf-8').splitlines():
        line = raw.strip()
        if line.startswith('///'):
            doc.append(line)
            continue
        if not line or line.startswith('//') or line.startswith('using ') or line.startswith('namespace '):
            continue
        m = re.match(r'public (?:sealed |unsafe |static )*(class|interface|enum) (\w+)', line)
        if m and depth == 0:
            current = {'kind': m.group(1), 'name': m.group(2), 'doc': clean_doc(doc), 'members': []}
            types.append(current)
            doc = []
            if current['kind'] == 'enum' and '{' in line and '}' in line:
                values = line[line.index('{') + 1:line.rindex('}')]
                current['members'] = [{'sig': v.strip(), 'doc': '', 'name': v.split('=')[0].strip()} for v in values.split(',') if v.strip()]
                continue
            depth += line.count('{') - line.count('}')
            continue
        opened, closed = line.count('{'), line.count('}')
        if current is not None and depth == 1 and line not in ('{', '}'):
            pending = (pending + ' ' + line).strip() if pending else line
            complete = pending.endswith(';') or pending.endswith('}') or (pending.endswith(')') and current['kind'] == 'class')
            if complete:
                sig = re.sub(r'^public\s+', '', pending)
                sig = re.sub(r'\s*=>.*$', '', sig).rstrip(';').strip()
                if sig.endswith(')') and current['kind'] == 'class' and opened == 0 and not pending.endswith(';'):
                    pass  # a constructor whose body follows on the next lines
                name = re.search(r'(\w+)\s*(?:<[^>]*>)?\s*(?:\(|\{|$)', sig)
                current['members'].append({'sig': sig, 'doc': clean_doc(doc), 'name': name.group(1) if name else sig})
                doc, pending = [], ''
        depth += opened - closed
        if depth == 0:
            current = None
            doc = []
    return types


GROUPS = [
    ('Mods', ['IFairwayMod', 'IModContext', 'ILog', 'IModManager', 'ModEntry', 'IActionRegistry', 'IMainThread']),
    ('The game\'s interface', ['IGameUi', 'IMainMenu', 'MenuTab', 'GameTile', 'MenuTile', 'MenuBanner', 'MenuChoice']),
    ('Custom screens', ['IUiScreen', 'IUiCanvas', 'UiAlign', 'UiGradient', 'UiImage']),
    ('Courses', ['ICourseCatalog', 'CourseInfo', 'MatchHoles']),
    ('Game objects', ['IGameRuntime']),
]


def reference(types):
    by_name = {t['name']: t for t in types}
    listed = {n for _, names in GROUPS for n in names}
    groups = GROUPS + [('Other', [t['name'] for t in types if t['name'] not in listed])]
    parts, nav = [], []
    for title, names in groups:
        names = [n for n in names if n in by_name]
        if not names:
            continue
        nav.append(f'<div class="nav-group">{html.escape(title)}</div>')
        parts.append(f'<h3 class="ref-group">{html.escape(title)}</h3>')
        for name in names:
            t = by_name[name]
            nav.append(f'<a href="#ref-{name}" class="nav-ref">{name}</a>')
            rows = []
            for m in t['members']:
                sig = csharp(m['sig']) if t['kind'] != 'enum' else f'<span class="t">{html.escape(m["sig"])}</span>'
                doc = f'<p>{m["doc"]}</p>' if m['doc'] else ''
                rows.append(f'<div class="member"><code class="sig">{sig}</code>{doc}</div>')
            summary = f'<p class="lead">{t["doc"]}</p>' if t['doc'] else ''
            parts.append(f'<section class="ref" id="ref-{name}"><h4><span class="kind">{t["kind"]}</span>{name}</h4>{summary}'
                         f'<div class="members">{"".join(rows)}</div></section>')
    return '\n'.join(parts), '\n'.join(nav)


# ---- the page --------------------------------------------------------------------------------------------------

MOD_JSON_NEEDS = '''{
  "openfairway": "0.5.0",
  "requires": ["OfflineCareer"]
}'''

INSTALL_STEPS = r'''dotnet build -c Release
mkdir "<game>\OpenFairway\mods\HelloBanner"
copy bin\Release\HelloBanner.dll "<game>\OpenFairway\mods\HelloBanner"
copy mod.json "<game>\OpenFairway\mods\HelloBanner"'''

def guide():
    mod_json = json.dumps({'name': 'Hello Banner', 'version': '1.0.0', 'author': 'You', 'description': 'The smallest useful mod.',
                           'icon': 'Icon_Play'}, indent=2)
    csproj = '''<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <EnableDynamicLoading>true</EnableDynamicLoading>
  </PropertyGroup>
  <ItemGroup>
    <!-- Private=false: the game already loads OpenFairway.API.dll; never ship your own copy. -->
    <Reference Include="OpenFairway.API" HintPath="path\\to\\OpenFairway.API.dll" Private="false" />
  </ItemGroup>
</Project>'''
    sections = []

    def section(anchor, title, body):
        sections.append(f'<section id="{anchor}"><h2>{title}</h2>{body}</section>')

    section('overview', 'Overview', f'''
<p class="lead">OpenFairway mods are small <b>C#</b> libraries for <b>.NET 8</b>. A mod is one DLL and a
<code>mod.json</code> in its own folder under <code>&lt;game&gt;\\OpenFairway\\mods\\</code>. The launcher and the in-game
mod manager list every mod with an on/off switch.</p>
<div class="facts">
  <div><span>Language</span>C#, .NET 8</div>
  <div><span>A mod is</span>One DLL + mod.json</div>
  <div><span>It can use</span>Menus, popups, custom screens, courses, game objects</div>
  <div><span>Runs</span>Offline, in memory only</div>
</div>
<h3>How a mod gets loaded</h3>
{block("""Steam starts PGA TOUR 2K25 (only from the OpenFairway launcher does the loader switch on)
  Unity Doorstop (version.dll) starts .NET 8 inside the game process
    OpenFairway.Core
      offline guard     the game cannot reach 2K's servers, or OpenFairway stays inactive
      runtime bridge    talks to the game through its own IL2CPP runtime (il2cpp_* exports)
      type dump         writes OpenFairway\\dump\\*.cs: every game class, field and method signature
      mod loader        loads mods\\<Name>\\<Name>.dll and calls OnLoad on its IFairwayMod""", 'text')}
<p>Nothing on disk is changed: the game files stay as Steam installed them, and all changes live in memory for one
session. Without the launcher, the game starts normally and online.</p>''')

    section('first-mod', 'Your first mod', f'''
<p>Implement <a href="#ref-IFairwayMod"><code>IFairwayMod</code></a>: an <code>Id</code>, and <code>OnLoad</code>, which runs
once when the game is up. Everything the platform offers comes in through <a href="#ref-IModContext"><code>IModContext</code></a>.</p>
{example("HelloBanner")}
<p>Next to the DLL goes a <code>mod.json</code>. The <code>icon</code> is the name of one of the game's own interface
sprites; the mod manager shows the rest.</p>
{block(mod_json, 'json', 'mod.json')}
<p>Two optional fields say what a mod needs. <code>openfairway</code> is the oldest OpenFairway it works with: on an older
one the platform leaves the mod out and says why, and the rest still load. <code>requires</code> lists the folders of mods
it needs; the launcher marks the mod when one of them is off or missing, and the log says so too.</p>
{block(MOD_JSON_NEEDS, 'json', 'mod.json (optional fields)')}
<p>Players can install a mod by dropping its <code>.zip</code> (or its folder) on the launcher. A zip that holds the mod's
folder, or just its DLL and <code>mod.json</code>, both work.</p>''')

    section('build', 'Build and install', f'''
<p>A mod project only needs .NET 8 and a reference to <code>OpenFairway.API.dll</code>, which ships with OpenFairway in
<code>&lt;game&gt;\\OpenFairway\\core\\</code>. The examples in this repo use <code>examples/Directory.Build.props</code>
instead; copy one of them to start.</p>
{block(csproj, 'text', 'HelloBanner.csproj')}
{block(INSTALL_STEPS, 'text', 'Install')}
<p>Start the game from the OpenFairway launcher. Logs are in <code>&lt;game&gt;\\OpenFairway\\logs\\openfairway.log</code>,
with every line from <code>ctx.Log</code> tagged with your mod's id.</p>
<div class="note"><b>Copy only your DLL and mod.json.</b> A copy of <code>OpenFairway.API.dll</code> in your mod's folder
would be a second, different API and the mod would not load.</div>''')

    section('menus', 'The main menu', f'''
<p><a href="#ref-IMainMenu"><code>ctx.Ui.MainMenu</code></a> rebuilds the game's own menu: which tabs show, the tiles on
each tab (yours or the game's), and the banner. These are rules the platform keeps applying, because the game rebuilds
its menus often, so set them once in <code>OnLoad</code>.</p>
{example("PracticeMenu")}
<p>Popups are the game's own too: <code>ShowNotice</code> for a message, <code>ShowChoices</code> for a list,
<code>ReplacePopup</code> to rewrite or silence one of the game's popups by its internal name, and
<code>ReplaceText</code> for any text on screen.</p>
<div class="note">Two mods setting the same tab's tiles will take turns overwriting each other. The bundled Offline Main
Menu mod owns the HOME tab; switch it off while you try a menu example.</div>''')

    section('screens', 'Custom screens', f'''
<p><a href="#ref-IGameUi"><code>ctx.Ui.CreateScreen()</code></a> gives you a full screen drawn with the engine's UI
components in the game's own fonts, on a fixed 1920x1080 canvas that the platform scales to the window. It handles the
mouse, keyboard and gamepad (with a focus ring), Escape/B for back, and blocks the game's input while it is open.
The Climb and the mod manager are built this way.</p>
<p>You draw the whole screen in <code>Render</code> and call <code>Invalidate()</code> when something changes. Each draw
builds the page again; numbers, meters and <code>Slide</code> fills remember their last value by key and animate to the new one.</p>
{example("CourseBrowser")}
<h3>What the canvas draws</h3>
<table>
<tr><th>Call</th><th>For</th></tr>
<tr><td><code>Rect</code>, <code>Panel</code>, <code>Outline</code>, <code>Gradient</code>, <code>Shadow</code></td><td>Shapes. Colours are 0xRRGGBBAA.</td></tr>
<tr><td><code>Label</code>, <code>Number</code></td><td>Text in the game's body or heading font; numbers count to new values.</td></tr>
<tr><td><code>Image</code>, <code>Picture</code>, <code>Backdrop</code>, <code>Icon</code>, <code>Sprite</code></td><td>Course photos, your PNGs, a slowly drifting backdrop, the game's own icons.</td></tr>
<tr><td><code>Button</code>, <code>Clickable</code>, <code>Hint</code></td><td>Things to click, with hover, press, keyboard focus and tooltips.</td></tr>
<tr><td><code>Group</code>, <code>Slide</code>, <code>Meter</code>, <code>Ring</code></td><td>Entrance animation, a fill that glides between rows, progress.</td></tr>
</table>
<div class="note">The game draws its interface in linear colour space, so translucent white shows up as mid grey.
Use opaque colours for fills and keep translucent layers dark.</div>''')

    section('courses', 'Courses and matches', f'''
<p><a href="#ref-ICourseCatalog"><code>ctx.Ui.Courses</code></a> lists the courses that ship with the game, read from
its own data at run time: name, par, holes, length, difficulty and its preview photo. <code>OpenMatch</code> opens the
game's local match screen on a course, ready to begin; call it with the main menu in front.</p>
{block("""var course = ctx.Ui.Courses.All.First(c => c.Name.Contains("St Andrews"));
ctx.Ui.Courses.OpenMatch(course.Id, MatchHoles.FrontNine);""")}''')

    section('actions', 'Mods working together', f'''
<p><a href="#ref-IActionRegistry"><code>ctx.Actions</code></a> lets a mod offer something by name, and any other mod call
it without referencing its DLL. <code>Invoke</code> returns false when nobody offers the action, so a feature can say
which mod to switch on.</p>
{block("""// In one mod
ctx.Actions.Register("coursebrowser.open", Open);

// In another
if (!ctx.Actions.Invoke("coursebrowser.open"))
    ctx.Ui.ShowNotice("COURSES", "Switch on the Course Browser mod.");""")}
<table>
<tr><th>Action</th><th>Offered by</th><th>Does</th></tr>
<tr><td><code>career.open</code></td><td>Offline Career</td><td>Opens the game's MyCAREER offline</td></tr>
<tr><td><code>player.open</code></td><td>Offline Career</td><td>Opens MyPLAYER</td></tr>
<tr><td><code>player.addxp</code> <i>n</i></td><td>Offline Career</td><td>Gives the golfer n MyPLAYER experience</td></tr>
<tr><td><code>player.addequipmentxp</code> <i>n</i></td><td>Offline Career</td><td>Gives the active golf bag n club and ball experience</td></tr>
<tr><td><code>mods.open</code></td><td>Offline Main Menu</td><td>Opens the in-game mod manager</td></tr>
<tr><td><code>climb.open</code></td><td>The Climb</td><td>Opens The Climb</td></tr>
</table>''')

    section('state', 'Timing, saving and game objects', f'''
<p>Game objects may only be touched on the game's main thread. <a href="#ref-IMainThread"><code>ctx.MainThread</code></a>
gives you a per-frame <code>Update</code>, a <code>Quitting</code> event for saving, and <code>Post</code> to get onto
the main thread from anywhere. <code>ctx.DataDirectory</code> is a folder for your mod's files.</p>
<p><a href="#ref-IGameRuntime"><code>ctx.Runtime</code></a> reaches the game itself: find classes and methods by name,
call them, read and write fields. What exists is listed in <code>&lt;game&gt;\\OpenFairway\\dump\\*.cs</code>, which the
platform writes from the running game (signatures only, no code). This example uses all three.</p>
{example("RoundCounter")}
<div class="note"><b>Handles are raw pointers into the game.</b> Do not keep the game's objects between frames unless
you pin them (<code>Runtime.Pin</code>): the game's garbage collector does not know about your references. Pass only
pointers you got from the runtime; a wrong pointer crashes the game.</div>''')

    section('rules', 'Rules', '''
<ul class="rules">
<li><b>Offline only.</b> Mods run only when the game was started from the OpenFairway launcher and cannot reach 2K's servers.</li>
<li><b>Nothing paid.</b> No VC, store items, passes or DLC. Mods that unlock paid content are out of scope and are not accepted.</li>
<li><b>Memory only.</b> Never change, replace or redistribute the game's files, and never share dump output or game assets.</li>
</ul>''')
    return '\n'.join(sections)


PAGE = '''<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>OpenFairway modding</title>
<meta name="description" content="How to write OpenFairway mods for PGA TOUR 2K25: C# on .NET 8, examples and the full API reference.">
<style>
:root { --bg:#0F0F1D; --ink:#0A0A14; --strip:#191923; --tile:#242731; --line:#2E3140; --text:#F4F4F4; --muted:#AEB4C6;
        --faint:#6E7486; --cream:#FDEDDA; --red:#E0323F; --gold:#E9B949; --kw:#8FA3D9; --type:#E7E9EE; --str:#F3D9B8;
        --fn:#B9D3F0; --num:#E9B949; --com:#6E7486; }
* { box-sizing: border-box; }
html { scroll-behavior: smooth; scroll-padding-top: 24px; }
body { margin: 0; background: var(--bg); color: var(--text); font: 16px/1.65 "Segoe UI", system-ui, -apple-system, sans-serif; }
a { color: var(--cream); text-decoration: none; }
a:hover { text-decoration: underline; }
code { font-family: "Cascadia Mono", Consolas, "SF Mono", Menlo, monospace; font-size: 0.88em; background: var(--strip);
       padding: 1px 6px; border-radius: 4px; color: var(--type); }
.layout { display: grid; grid-template-columns: 280px minmax(0, 1fr); min-height: 100vh; }
nav { position: sticky; top: 0; height: 100vh; overflow-y: auto; background: var(--ink); border-right: 1px solid var(--line); padding: 28px 22px 40px; }
nav .brand img { width: 200px; display: block; margin-bottom: 6px; }
nav .brand div { color: var(--faint); font-size: 13px; margin-bottom: 26px; }
nav a { display: block; color: var(--muted); padding: 4px 10px; border-radius: 4px; font-size: 14.5px; }
nav a:hover { color: var(--text); background: var(--strip); text-decoration: none; }
nav a.active { color: var(--ink); background: var(--cream); }
nav .nav-group { color: var(--faint); font-size: 11.5px; font-weight: 700; letter-spacing: .08em; text-transform: uppercase; margin: 22px 10px 6px; }
nav .nav-ref { font-family: "Cascadia Mono", Consolas, monospace; font-size: 13px; }
main { padding: 48px 64px 120px; max-width: 980px; }
h1 { font-weight: 900; font-style: italic; font-size: 44px; letter-spacing: -.01em; margin: 0 0 6px; }
h2 { font-weight: 900; font-style: italic; text-transform: uppercase; font-size: 28px; margin: 64px 0 14px; letter-spacing: .01em; }
h3 { font-size: 18px; margin: 30px 0 10px; }
.sub { color: var(--muted); font-size: 18px; margin: 0 0 18px; }
.lead { font-size: 17.5px; color: #DADDE5; }
.hero-links a { display: inline-block; margin: 6px 10px 0 0; padding: 8px 16px; border-radius: 999px; background: var(--cream); color: var(--ink); font-weight: 600; }
.hero-links a.ghost { background: transparent; color: var(--text); border: 1px solid var(--line); }
.facts { display: grid; grid-template-columns: repeat(4, 1fr); gap: 12px; margin: 22px 0 8px; }
.facts div { background: var(--strip); border: 1px solid var(--line); border-radius: 6px; padding: 14px 16px; font-weight: 600; }
.facts span { display: block; color: var(--faint); font-size: 11.5px; font-weight: 700; letter-spacing: .08em; text-transform: uppercase; margin-bottom: 4px; }
.code { position: relative; background: var(--ink); border: 1px solid var(--line); border-radius: 8px; margin: 16px 0; overflow: hidden; }
.code-title { font-size: 13px; color: var(--muted); padding: 8px 16px; background: var(--strip); border-bottom: 1px solid var(--line); font-family: "Cascadia Mono", Consolas, monospace; }
.code pre { margin: 0; padding: 16px 18px; overflow-x: auto; }
.code pre code { background: none; padding: 0; font-size: 13.5px; line-height: 1.6; color: #C9CEDA; }
.copy { position: absolute; top: 6px; right: 8px; background: var(--tile); color: var(--muted); border: 1px solid var(--line);
        border-radius: 4px; font-size: 12px; padding: 3px 10px; cursor: pointer; }
.copy:hover { color: var(--text); }
.k { color: var(--kw); } .t { color: var(--type); } .s { color: var(--str); } .f { color: var(--fn); } .n { color: var(--num); }
.c { color: var(--com); font-style: italic; } .p { color: var(--kw); }
.note { border-left: 3px solid var(--cream); background: var(--strip); padding: 12px 16px; border-radius: 0 6px 6px 0; margin: 16px 0; color: #DADDE5; }
table { width: 100%; border-collapse: collapse; margin: 14px 0; font-size: 15px; }
th { text-align: left; color: var(--faint); font-size: 12px; letter-spacing: .06em; text-transform: uppercase; padding: 8px 10px; border-bottom: 1px solid var(--line); }
td { padding: 9px 10px; border-bottom: 1px solid var(--line); vertical-align: top; }
.rules li { margin: 8px 0; }
.ref-group { margin-top: 44px; color: var(--muted); font-size: 13px; letter-spacing: .08em; text-transform: uppercase; }
.ref { background: var(--strip); border: 1px solid var(--line); border-radius: 8px; padding: 20px 22px; margin: 14px 0; }
.ref h4 { margin: 0 0 6px; font-size: 21px; font-family: "Cascadia Mono", Consolas, monospace; }
.ref .kind { font-family: "Segoe UI", system-ui, sans-serif; font-size: 11px; font-weight: 700; letter-spacing: .08em; text-transform: uppercase;
             color: var(--ink); background: var(--cream); padding: 2px 8px; border-radius: 4px; margin-right: 10px; vertical-align: 3px; }
.ref .lead { font-size: 15.5px; margin: 4px 0 10px; }
.member { padding: 10px 0; border-top: 1px solid var(--line); }
.member .sig { display: block; background: none; padding: 0; font-size: 13.5px; white-space: pre-wrap; color: #C9CEDA; }
.member p { margin: 4px 0 0; color: var(--muted); font-size: 14.5px; }
footer { color: var(--faint); font-size: 13px; margin-top: 80px; border-top: 1px solid var(--line); padding-top: 20px; }
@media (max-width: 900px) {
  .layout { grid-template-columns: 1fr; }
  nav { position: static; height: auto; border-right: 0; border-bottom: 1px solid var(--line); }
  main { padding: 28px 16px 80px; }
  .facts { grid-template-columns: repeat(2, 1fr); }
  h1 { font-size: 34px; }
}
</style>
</head>
<body>
<div class="layout">
<nav>
  <div class="brand"><img src="images/wordmark.png" alt="OpenFairway"><div>Modding guide and API, v%%VERSION%%</div></div>
  <div class="nav-group">Guide</div>
  <a href="#overview">Overview</a>
  <a href="#first-mod">Your first mod</a>
  <a href="#build">Build and install</a>
  <a href="#menus">The main menu</a>
  <a href="#screens">Custom screens</a>
  <a href="#courses">Courses and matches</a>
  <a href="#actions">Mods working together</a>
  <a href="#state">Timing, saving, game objects</a>
  <a href="#rules">Rules</a>
  <div class="nav-group">API reference</div>
  %%NAV%%
</nav>
<main>
  <h1>Write mods for OpenFairway</h1>
  <p class="sub">C# on .NET 8, running inside your own offline copy of PGA TOUR 2K25.</p>
  <div class="hero-links"><a href="%%REPO%%">Source on GitHub</a><a class="ghost" href="%%REPO%%/tree/main/examples">Example mods</a><a class="ghost" href="%%REPO%%/releases">Download</a></div>
  %%GUIDE%%
  <section id="reference"><h2>API reference</h2>
  <p>Everything in <code>OpenFairway.API.dll</code>, namespace <code>OpenFairway.API</code>. Generated from the source and its doc comments.</p>
  %%REFERENCE%%
  </section>
  <footer>OpenFairway is an unofficial community project, not affiliated with 2K, HB Studios or the PGA TOUR. MIT licence.</footer>
</main>
</div>
<script>
document.querySelectorAll('.copy').forEach(function (button) {
  button.addEventListener('click', function () {
    var text = button.parentElement.querySelector('pre').innerText;
    navigator.clipboard.writeText(text).then(function () { button.textContent = 'Copied'; setTimeout(function () { button.textContent = 'Copy'; }, 1400); });
  });
});
var links = Array.prototype.slice.call(document.querySelectorAll('nav a[href^="#"]'));
var targets = links.map(function (a) { return document.querySelector(a.getAttribute('href')); });
function mark() {
  var current = 0;
  targets.forEach(function (t, i) { if (t && t.getBoundingClientRect().top < 120) current = i; });
  links.forEach(function (a, i) { a.classList.toggle('active', i === current); });
}
document.addEventListener('scroll', mark, { passive: true });
mark();
</script>
</body>
</html>
'''


def main():
    version = re.search(r'<Version>([^<]+)</Version>', (REPO / 'src' / 'OpenFairway.Core' / 'OpenFairway.Core.csproj').read_text()).group(1)
    reference_html, nav = reference(read_api())
    page = (PAGE.replace('%%GUIDE%%', guide()).replace('%%REFERENCE%%', reference_html).replace('%%NAV%%', nav)
            .replace('%%REPO%%', REPO_URL).replace('%%VERSION%%', version))
    OUT.write_text(page, encoding='utf-8')
    print('wrote', OUT, f'({len(page) // 1024} KB)')


if __name__ == '__main__':
    main()
