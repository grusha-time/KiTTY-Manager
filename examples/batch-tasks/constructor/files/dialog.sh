#!/bin/sh
printf 'Name%s' ':'
IFS= read -r name
printf 'Total%s' ':'
IFS= read -r total
[ "$name" = alpha ] && [ "$total" = 87 ] || exit 1
printf 'dialog=ok\n' > "$1"
printf 'dialog-finished\n'
