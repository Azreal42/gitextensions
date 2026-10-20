# ADR 0124 — Choisir, forker et cloner un dépôt GitHub

ForkCloneDialog raccorde le menu Git hosts au client Hosting existant : My repositories, Search/Get from user, détails et parent, puis clone Git réel et upstream optionnel. Les identifiants restent ceux du profil Hosting, distincts de CI Actions ; aucune découverte de compte machine.

Un POST explicite accepte un fork ; son résultat reste sélectionné même si la liste secondaire échoue. Une issue réseau incertaine n’autorise aucun rejeu automatique. Les détails et le parent doivent garder l’identité demandée. Les lectures et le processus Git sont annulables et drainés avec le propriétaire.

La destination, le protocole, la profondeur et le remote viennent des choix visibles. Un clone acquis avec ajout upstream échoué reste un dépôt utilisable avec avertissement, sans rollback global fabriqué. Les lecteurs UI conservent les URL de l’hébergeur ; un transport local de fixture ne certifie pas GitHub réel.

N2 partiel ; autres hébergeurs, permissions et disponibilité distantes restent ouverts. [Preuves et périmètre](../../../todo.md#portable-workflows--perimetre-verifie).
