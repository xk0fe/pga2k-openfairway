# source after dev-lib.sh: find models of the current screen by their internal name and click them.
# named <name> [depth]: pointer of the first model on the current screen with that inspectorName
named(){ local M; M=$(field $(screen) model); c "tree $M ${2:-10}" | awk -v want="$1" '
  match($0, /^ *[0-9A-F]+ MVC\.Model(Array)?$/) { n = match($0, /[^ ]/) - 1; split(substr($0, n + 1), p, " "); at[n] = p[1]; next }
  index($0, "<inspectorName>k__BackingField = \"" want "\"") { n = match($0, /[^ ]/) - 1; print at[n - 2]; exit }'; }
# clicknamed <name>: runs that model's click action
clicknamed(){ local M; M=$(named "$1"); [ -z "$M" ] && { echo "no model $1"; return 1; }; click $M; }
# actions [depth]: every click action on the current screen as "<delegate> <kind> <label> <name>"; kind is plain
# (System.Action) or node (Action<NodeAnchorPoint>: invoke with p:<screen node>)
actions(){ local M D; M=$(field $(screen) model); for D in $(c "tree $M ${1:-10}" | grep -A2 '"MVC.Bindings+OnClick"' | grep -oE 'data = [0-9A-F]+ MVC\.InteractiveAction' | awk '{print $3}' | sort -u); do
  c "peek $D 12" | awk '/^\+40/{l=$3} /^\+48/{n=$3} /^\+50/ && $2!="0"{a=$2; k="plain"} /^\+58/ && $2!="0"{a=$2; k="node"} END{if (a) print a, k, l, n}'; done; }
# act <label>: runs the first action whose label (or name) matches
act(){ local L; L=$(actions | grep -F "\"$1\"" | head -1); [ -z "$L" ] && { echo "no action $1"; return 1; }; set -- $L; if [ "$2" = node ]; then c "invoke $1 p:$(field $(screen) node)"; else c "invoke $1"; fi; }
# helpbar: the current screen's help-bar buttons that have an action ("<delegate> <index>"); press [n]: runs the
# n-th of them (0 by default) with the screen's state, as pressing its key would
helpbar(){ local CT HB S A B; CT=$(field $(screen) controller); HB=$(c "fields $CT" | grep -E "HelpBarLayout [a-zA-Z_]+ = " | head -1 | awk '{print $(NF-1)}'); [ -z "$HB" ] && return 1
  for S in $(c "arr $(field $HB rightButtons)" $(:) | grep -oE "^\[[0-9]+\] [0-9A-F]+" | awk '{print $2}') $(field $HB midButton); do A=$(field $S _array); [ -z "$A" ] || [ "$A" = null ] && continue
    for B in $(c "arr $A" | grep -oE "^\[[0-9]+\] [0-9A-F]+" | awk '{print $2}'); do c "tree $B 1" | awk -v b=$B '/index = /{i=$3} /action = delegate/{print $4, i}'; done; done; }
press(){ local D; D=$(helpbar | sed -n "$((${1:-0}+1))p" | awk '{print $1}'); [ -z "$D" ] && { echo "no button"; return 1; }; c "invoke $D p:$(field $(field $(screen) controller) "<screenState>k__BackingField")"; }
