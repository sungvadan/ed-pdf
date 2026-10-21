#!/bin/bash
# Runs the text-editing scenarios on build/fixtures (see docs/text-editing.md). Output PDFs go to $1 (default /tmp).
set -uo pipefail
cd "$(dirname "$0")/.."
OUT="${1:-/tmp}"
T=.build/debug/textlab
F=build/fixtures
pass=0; fail=0
while IFS='|' read -r file block find repl name; do
    [ -z "$file" ] && continue
    result=$($T replace "$F/$file" 1 "$block" "$find" "$repl" "$OUT/$name.pdf" 2>&1)
    if [ $? -eq 0 ] || [[ "$result" == *"new text OK"* ]]; then pass=$((pass + 1)); status=OK; else fail=$((fail + 1)); status=FAIL; fi
    printf "%-5s %-4s %s\n" "$status" "$name" "$(echo "$result" | grep font: | sed 's/font: //' | tr '\n' ';')"
    [ $status = FAIL ] && echo "      $result" | head -3
done <<'CASES'
lettre.pdf|2|trois mois|deux mois|e1
lettre.pdf|2|trois mois|ZKW semaines|e2
lettre.pdf|0|2024-118|2025-042 (avenant)|e4
colonnes.pdf|1|corriger une coquille|corriger rapidement une petite coquille typographique|e3
colonnes.pdf|1|corriger une coquille|corriger une erreur|e6
facture.pdf|0|FA-2026-0312|FA-2026-0400|f1
facture.pdf|2|trente jours|quarante-cinq jours (paiement à réception accepté)|f2
facture.pdf|2|trente jours|vingt jours|f5
facture.pdf|10|7 800,00 €|8 125,50 €|f3
facture.pdf|1|Société Exemple SARL|Société Durand & Fils|f4
facture.pdf|16|trois fois|deux fois|f6
divers.pdf|0|officiel|officiel et efficace|d1
divers.pdf|2|Jean Dupont|Jeanne Dupuis-Martin|d2
divers.pdf|7|30 €|45 € ✓|d3
divers.pdf|4|Paris|Paris ✓ ★|d4
formulaire.pdf|0|2026|2027|m1
formulaire.pdf|0|INITIALE|INITIALE MODIFICATIVE|m2
CASES
echo "$pass réussis, $fail échoués"
