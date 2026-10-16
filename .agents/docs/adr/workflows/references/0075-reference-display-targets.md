# ADR 0075 · Identité et rattachement des références

Statut : accepté sur ce périmètre ; Browse reste N2.

Besoin : les tags annotés doivent apparaître sur leur commit dans les badges, le graphe et Copy > Tags. Une réannotation vers le même commit doit néanmoins invalider un choix de mutation déjà ouvert.

Décision : conserver Hash/Sha1Id comme identité brute et exposer RevisionHash pour le rattachement. GetReferences et QuickReferences partagent un lecteur fondé sur l’expression native Windows ; ne pas inventer une profondeur de peeling. Le premier garde tous les namespaces et ses couleurs ; le second conserve une lecture sans verrou, limitée aux trois namespaces d’action et aux cibles commit.

Les badges créés ou actualisés, le graphe et Copy > Tags utilisent cette cible. Le point commun de présentation réactualise aussi le tooltip avec les références, en conservant sujet et corps du commit. Les gardes de mutation restent sur nom/hash brut.

Repères : GitModels, GitRepository.ReferenceTargets/QuickReferences, RevisionHistoryRow, CommitGraph et MainWindow.RevisionMenu. Ciblés sur vrai Git et rendu headless verts, ancien rattachement reproduit rouge puis restauré vert. Suites complètes et contrôle natif ciblé du paquet a62 validés ; preuves et limites dans le TODO. Le retour de focus après fermeture du sous-menu Copy reste un écart distinct ouvert.
