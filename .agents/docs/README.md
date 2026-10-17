# Repères

But : proposer les workflows Git Extensions sur un bureau Linux, installables en `.deb`, en gardant la disposition, les palettes, les icônes et les gestes de l’application originale. La migration reste ouverte tant que les workflows existants ne sont pas couverts.

- [C4](c4.md) : périmètre et points d'entrée.
- [Décisions](adr/) : classées par sujet/sous-sujet.
- [TODO](todo.md) : périmètre restant et état de livraison.
- [Inventaire Windows → Linux](windows-linux-window-inventory.md) : chaque fenêtre/page, équivalent réel, niveau N0–N4, écarts et preuves ; CSV/JSON et contrôle reproductible ; dialogues de l’installateur et travaux non validés séparés.
- [Failures](failure.md) : causes et pièges à ne pas reproduire.
- [Menus de fichiers et récupération Skip/Assume](../../artifacts/linux/snapshots/a0ba1c140/proofs/source-validation.json) et [paquet isolé](../../artifacts/linux/snapshots/a0ba1c140/proofs/package-runtime-verification.json) : Desktop complet et références Core/Plugins distinctes ; périmètre natif dans le TODO. FormCommit/Browse/FormDiff restent N2, aucun N4 ; installation hôte distincte.

Le code, les tests et les scripts sont la source de vérité. Ces notes gardent l'intention et les chemins utiles ; les logs, captures, checksums et preuves historiques restent dans `artifacts/linux/`, avec leur source d'origine.
