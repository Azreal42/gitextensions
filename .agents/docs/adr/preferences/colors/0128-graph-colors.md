# ADR 0128 — Publier les options visuelles du graphe

ColorsSettingsPage raccorde cinq des six choix Graph Windows aux consommateurs existants : alternance du fond, gris des non-apparentés, gris de leur texte, révisions de l’auteur et fond des labels de références. La sixième case Windows Multicolor branches sauvegarde encore une préférence, mais le renderer Windows courant du dépôt ne la lit plus : ne pas inventer une couleur uniforme ni ajouter une case sans effet. Les options graphiques fonctionnelles sont portées ; CSS/thèmes/variations Windows restent distincts du thème Linux dans Appearance.

ColorsSettingsSession distingue la vue active des défauts sauvegardés. Seuls les contrôles modifiés deviennent des intentions durables ; une vue temporaire inchangée ne devient pas implicitement un défaut. HistoryViewPreferencesStore garde inconnus et champs voisins, fusionne les intentions non conflictuelles et refuse les conflits observés. Apply publie sans reconstruire les lignes ni perdre sélection ou lectures différées ; Cancel abandonne le brouillon restant.

Les intents View et Appearance utilisent le même baseline sauvegardé, afin de préserver une publication concurrente et de ne pas réécrire tout le profil. N2 partiel ; [preuves et périmètre](../../../todo.md#portable-workflows--perimetre-verifie).

Repères de cet audit statique : AppSettings.MulticolorBranches ; ColorsSettingsPage.LoadSettings/SaveSettings ; RevisionGraphLaneColor et GraphRenderer. Aucun niveau ni résultat de rendu déduit de cette seule lecture.
