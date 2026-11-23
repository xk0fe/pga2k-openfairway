# source this: helpers for the developer console
OF_REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
mkdir -p "$OF_REPO/shots"
c(){ python -I "$OF_REPO/tools/devcon.py" "$@" 2>/dev/null; }
first(){ c "find $1" | grep -E '^[0-9A-F]+ ' | head -1 | awk '{print $1}'; }
field(){ c "fields $1" | grep -E " $2 = " | head -1 | sed 's/.* = //' | awk '{print $1}'; }
shot(){ c "shot $(cygpath -m "$OF_REPO/shots")/$1.png" >/dev/null; sleep 1; }
# close every open popup through its own cancel handler
dismiss(){ local P TOP D i; P=$(first "@HB.Golf|MVC|PopupContainer"); for i in 1 2 3 4 5; do TOP=$(field $P topPopup); [ -z "$TOP" ] || [ "$TOP" = "null" ] && break; D=$(c "tree $TOP 3" | grep -m1 "onCancel = delegate" | awk '{print $4}'); [ -z "$D" ] && break; c "invoke $D" >/dev/null; sleep 2; done; }
# click delegate of a tile/row model
tileaction(){ local LD A; LD=$(c "tree $1 3" | grep -A1 "\[MVC.Bindings+OnClick\]" | tail -1 | awk '{print $1}'); A=$(field $LD data); c "peek $A 12" | grep -E "System\.Action(<.*>)?$" | head -1 | awk '{print $2, $NF}'; }
alive(){ tasklist //FI "IMAGENAME eq PGATOUR2K25.exe" | tail -1 | cut -c1-16; }
# current screen helpers
screen(){ local SC; SC=$(first "@HB.Golf|MVC|ScreenContainer"); field $SC currentScreen; }
stack(){ c "list $(c "statics @HB.Golf|MVC|ScreenContainer" | grep screenStack | awk '{print $(NF-1)}')" | grep -oE '"[^"]+"' | tr '\n' ' '; echo; }
# list rows (model pointer + label) of the current screen that have a click action
items(){ local M; M=$(field $(screen) model); c "tree $M 14" > "$OF_REPO/shots/_screen.txt"; awk '/^ *[0-9A-F]+ MVC\.Model(Array)?$/{m=$1} /inspectorName/{n=$0; sub(/.*= /,"",n); name[m]=n} /\[MVC.Bindings\+Name\]/{inname=1} inname && /localized = /{t=$0; sub(/.*localized = /,"",t); label[m]=t; inname=0} /\[MVC.Bindings\+OnClick\]/{print m, name[m], label[m]}' "$OF_REPO/shots/_screen.txt" | sort -u; }
click(){ local T; T=($(tileaction $1)); if [[ "${T[1]}" == *"<"* ]]; then c "invoke ${T[0]} p:0"; else c "invoke ${T[0]}"; fi; }
popup(){ local P; P=$(first "@HB.Golf|MVC|PopupContainer"); field $P topPopup; }
# click the current screen's row with this label
clickitem(){ local M; M=$(items | grep -F "\"$1\"" | head -1 | awk '{print $1}'); [ -z "$M" ] && { echo "no item $1"; return 1; }; click $M >/dev/null; }
# first-launch helper: close the notice and return the HOME tab's tile models
hometiles(){ local SC CS CTRL PANELS PM HT HM; SC=$(first "@HB.Golf|MVC|ScreenContainer"); CS=$(field $SC currentScreen); CTRL=$(field $CS controller); PANELS=$(field $CTRL panels); PM=$(field $PANELS _models); HT=$(c "arr $PM" | grep "\[0\]" | awk '{print $2}'); HM=$(field $HT _models); c "arr $HM" | grep -E "^\[" | awk '{print $2}'; }
errs(){ tail -n +$1 "$USERPROFILE/AppData/LocalLow/2K/PGA TOUR 2K25/Player.log" | grep -E "https?://|Exception|Failed|rror:" | cut -c1-190 | sort | uniq -c | sort -rn | head -${2:-8}; }
loglines(){ wc -l < "$USERPROFILE/AppData/LocalLow/2K/PGA TOUR 2K25/Player.log"; }
rclick(){ powershell -NoProfile -ExecutionPolicy Bypass -File "$OF_REPO/tools/dev-click.ps1" -X $1 -Y $2; }
# popup helpers: list the open choice popup's rows, click row N (0-based)
prows(){ local P TOP; P=$(first "@HB.Golf|MVC|PopupContainer"); TOP=$(field $P topPopup); [ -z "$TOP" ] || [ "$TOP" = "null" ] && return; c "tree $TOP 8" | grep -E "^ {16}[0-9A-F]+ MVC\.Model$" | awk '{print $1}'; }
plabels(){ local r i=0; for r in $(prows); do echo "$i: $(c "tree $r 4" | grep -m1 localized | sed 's/.*= //')"; i=$((i+1)); done; }
prow(){ local R; R=($(prows)); click ${R[$1]} >/dev/null; sleep ${2:-3}; }
# custom screens: click a point of the 1920x1080 space / press back
ui(){ c "uiclick $1 $2"; sleep ${3:-1}; }
key(){ powershell -NoProfile -ExecutionPolicy Bypass -File "$OF_REPO/tools/dev-key.ps1" -Key $1 | tail -1; sleep ${2:-0.6}; }
