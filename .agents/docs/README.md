# Repères

But : proposer les workflows Git Extensions sur un bureau Linux, installables en `.deb`, en gardant la disposition, les palettes, les icônes et les gestes de l’application originale. La migration reste ouverte tant que les workflows existants ne sont pas couverts.

- [C4](c4.md) : périmètre et points d'entrée.
- [Décisions](adr/) : classées par sujet/sous-sujet.
- [TODO](todo.md) : périmètre restant et état de livraison.
- [Inventaire Windows → Linux](windows-linux-window-inventory.md) : chaque fenêtre/page, équivalent réel, niveau N0–N4, écarts et preuves ; CSV/JSON et contrôle reproductible ; dialogues de l’installateur et travaux non validés séparés.
- [Failures](failure.md) : causes et pièges à ne pas reproduire.
- [Dernière validation complète Desktop](../../artifacts/linux/snapshots/2e3bba726/proofs/source-validation.json) et [paquet vérifié en isolation](../../artifacts/linux/snapshots/2e3bba726/proofs/package-runtime-verification.json) ; 1241/1241 ; saisie Compare et retours modaux ciblés vérifiés en natif, parité globale encore ouverte. Installation hôte distincte dans le TODO.

Le code, les tests et les scripts sont la source de vérité. Ces notes gardent l'intention et les chemins utiles ; les logs, captures, checksums et preuves historiques restent dans `artifacts/linux/`, avec leur source d'origine.
