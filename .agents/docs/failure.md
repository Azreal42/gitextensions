# Failure

| Observation | Cause | Sortie / statut |
| --- | --- | --- |
| `dotnet --info` via `/snap/bin/dotnet` échoue dans le sandbox ; aucun SDK réel installé | Lanceur Snap / confinement | SDK officiel 10.0.401 installé dans `.dotnet`, utiliser ce binaire. Résolu. |
| `global.json` : version `10.0` invalide | Version SDK incomplète | `10.0.100` + `latestFeature`. Résolu. |
| Solution originale cible `net10.0-windows` et WinForms | Architecture Windows, pas un défaut de packaging | UI et frontières OS à porter ; chantier ouvert. |
| Téléchargement via `curl` impossible | Outil absent | Utiliser `wget` ; SDK installé. Résolu. |
| Actions UI cliquables pendant un refresh, clic suivant perdu | Verrou busy sans état visuel | Désactiver actions pendant l'opération ; attendre idle dans tests. Corrigé, testé. |
| Stash de nom contenant newline laissé en place malgré exit 0 | Option globale `--literal-pathspecs` incompatible avec `stash push -u` | Limiter cette option aux commandes à pathspec ; test réel. Résolu. |
| Version Debian contient le chemin HOME | Expansion Bash de `~` dans substitution | Citer le caractère ; paquet vérifié/reproductible. Résolu. |
| Chemins Linux à octets non UTF8 | API .NET basée sur string UTF8 | Unicode valide couvert ; roundtrip d'octets invalides restant. Ouvert. |
| Installation APT locale non exécutée | sudo nécessite authentification, pas de conteneur installé | DPKG installé/lancé dans une racine isolée avec dépendances host ; APT sur OS vierge reste un gate CI. Ouvert. |
| Reparenting du filtre historique plante après reprise du layout original | Contrôle encore attaché au panel précédent | Détacher avant insertion ; parcours UI validé. Résolu. |
| Ouverture bare s’arrête après chargement de l’historique | `stash list` exige un worktree même en lecture | Lire le reflog de refs/stash ; test UI d’ouverture complète. Résolu. |
| Tableau plugin plante après suppression | Template Avalonia reçoit un élément null pendant le renouvellement | Garde null ; aperçu/annulation/suppression réelle validés. Résolu. |
| Badges du graphe gardent les couleurs claires | Rendu non invalidé au changement de thème | Invalidation et test de pixels clair/sombre. Résolu. |
| Indexer/désindexer tout touche des fichiers masqués par le filtre Commit | Action globale au lieu des éléments visibles | Chemins visibles uniquement, scénario avec index partiel et noms littéraux. Corrigé, testé. |
| Indexer un renommage modifié échoue : ancien pathspec absent | Ancien et nouveau noms transmis à git add après git mv | Indexer le nom courant ; désindexer les deux noms. Corrigé, test UI réel. |
| Sélections X11 restent orange malgré les palettes reprises | Accent OS encore consommé par les templates Fluent | Ressources des templates et texte sélectionné repris ; tests pixels/contraste et captures X11 claire/sombre. Résolu. |
| Annulation d’un outil externe rend la main avant la fin du processus | WaitForExit annulé ne collecte pas le processus tué | Tuer l’arbre puis attendre la sortie réelle ; test de PID. Résolu. |
| Impact conserve un fond clair en thème sombre | Recherche de ressource sans variante effective | Résoudre avec ActualThemeVariant ; pixels clairs/sombres testés. Résolu. |
| Petite image de hauteur impaire rendue floue | Centrage interne sur un demi-pixel | Dimensionner avant centrage et arrondir le layout ; taille/position mesurées sur les pixels. Résolu. |
| Tests de cancellation échouent pendant la lecture de procfs | Le descendant disparaît entre File.Exists et ReadLines | Relecture après IOException, état absent/zombie exigé et délai borné ; 54 scénarios passent. Résolu. |
| Skip am avance mais la ligne suivante est marquée appliquée | Un second conflit produit exit non nul après avancement | Relire état am et repérer les indices ; série à deux conflits testée. Résolu. |
| Tests Avalonia bloqués malgré opération annulée et fenêtre prête à fermer | Assert.ThrowsAsync attend synchroniquement une continuation capturée sur le dispatcher UI | Attendre normalement dans le test async, avec délai borné ; tests History/Blame et HTTP concernés. Résolu. |
| Le clonage réinitialise le sous-dossier saisi | TextChanged différé arrive après l’édition utilisateur | Observer TextProperty immédiatement ; clone réel vers le dossier choisi testé. Résolu. |
| Nom de clone encodé et avertissement moins lisible que l’original | URL non décodée, deux-points local traité comme SCP et couleur fixe | Distinguer URL/SCP/local, reprendre adaptation de contraste originale et templates XLF complets ; scénario rendu clair/sombre/français. Résolu. |
| Supprimer une référence sélectionnée dans la navigation bloque le refresh | Le filtre de révision automatique vise la ref disparue | Marquer les filtres de navigation et revenir à All quand la ref disparaît ; auteur/message/FirstParent conservés, saisie manuelle inchangée. Après Rename, reconstruire aussi SelectedItem et garder l’expansion des groupes. Scénarios réels verts. |
| Valeurs des champs numériques compacts invisibles malgré leur contenu correct | MinWidth interne Fluent incompatible avec les 61 px du dialogue original | Template numérique local compact ; valeurs, flèches et options réellement rendues en clair/sombre. Résolu. |
| Annuler l’ouverture laisse un backend différent des listes affichées | Dépôt remplacé avant la fin des lectures | Charger les métadonnées avant le changement ; vrai processus log annulé, dépôt/filtre/BASE/récents conservés et backend confirmé par F5. Résolu. |
| Filtrer une multisélection ajoute un commit non choisi | Fallback au premier résultat avant restauration du secondaire | Choisir d’abord un commit sélectionné survivant ; scénario filtre et comparaison parent/commit réel. Résolu. |
| Fermer le propriétaire pendant une mise à jour laisse le checkout ouvert | Avalonia interroge d'abord l'enfant busy, qui bloque la fermeture de son ancêtre | Propager la fermeture ancêtre, annuler/attendre Git puis fermer le checkout et son propriétaire ; PID et fenêtres testés. Résolu. |
| La deuxième confirmation AutoCompile est construite hors du thread UI | Le service poursuit sur worker après la première solution | Marshaler chaque confirmation sur le dispatcher UI ; deux solutions réelles après checkout/update testées. Résolu. |
| Message ISO-8859-1 déclaré mais octets UTF-8 écrits | Runner texte toujours UTF-8 malgré i18n.commitEncoding | Codec strict partagé, stdin binaire et brouillon atomique ; octets Git/template/draft, affichage et caractères incompatibles testés. Résolu. |
| Sujet du reflog mal affiché malgré --encoding=UTF-8 | Git ne recode pas %gs ; préfixe UTF-8 et sujet historique peuvent être mélangés | Lire les champs binaires et retrouver la première ligne avec le codec du commit ; action Unicode, Latin1 et changement ultérieur de config testés. Notes personnalisées ambiguës encore ouvertes. |
| Fermer une ancienne fenêtre écrase le nouveau MERGE_MSG | Message Git créé après ouverture, baseline éditeur incomplet | Conserver le texte affiché après template/initialisation ; merge externe, fermeture/réouverture et dépôt retiré testés. Résolu. |
| Icônes de la barre Commit absentes malgré Icon non null | Template Fluent ignore Icon pour les éléments de menu supérieurs | Dessiner les PNG originaux dans le header ; captures rendues claire/sombre vérifiées. Résolu. |
| Capture dite française garde les menus anglais | Culture modifiée dans une factory async, sans propagation à l'appelant | Fixer la culture dans le test avant construction ; vérifier le popup effectivement rendu. Résolu. |

| Confirmation de commit vide échoue puis tests suivants signalent fonts:SystemFonts | TemplateBinding C# enregistré à LocalValue ; exception d'ouverture masquée par le teardown Avalonia | Lier à BindingPriority.Template ; reproductions et workflows Commit passent sans modifier l'isolation ni les assertions. Résolu. |
| Chevrons et radios Clone tronqués malgré champs de 24 px | Marges et dimensions minimales internes Fluent plus grandes que le contrôle | Ressources de hauteur compactes et glyphe ComboBox centré ; limites mesurées et captures réellement rendues claire/sombre. Résolu. |
| Validation console annonce succès sans exécuter les scénarios | dotnet test ne lance pas les projets console IsTestProject=false | Exécuter Core et Plugins avec dotnet run --no-build --no-restore ; compter les scénarios dans leurs logs. Résolu. |

| Selection Filter change le périmètre de Stage all et masque des fichiers | Recherche littérale confondue avec la sélection regex du contrôle original | Combo éditable/MRU de session, regex sélectionnant Unstaged ; listes et Stage all restent complets. Scénarios Git réels et captures vérifiés. Résolu. |
| Alt est ignoré dans les premières vérifications de dialogue | Aucun enfant ne détient le focus après Show headless ; le handler natif exige un descendant focalisé | Poser le focus initial original après initialisation, sans reprendre le focus déjà acquis ; tester ensuite les déplacements avec de vrais événements Alt. Résolu. |

Ne conserver ici que les erreurs qui changent la suite du travail ou évitent de refaire une impasse.
| Série à trois sélections inéligible et bornes à quatre perdues | Tri par graphe au lieu de l’ordre choisi ; les bornes calculées par l’original n’étaient pas transmises | Garder primaire/ordre et bases explicites ; comparer à Git brut et vérifier F5. Résolu. |
| Refresh revient du range-diff à un fichier ordinaire | Groupe actif effacé avant reconstruction | Préserver le groupe et l’ordre des sélections ; scénarios à deux, trois et quatre révisions. Résolu. |
| Un fichier ressemblant à un patch peut être interprété comme un diff | Aperçu brut envoyé à l’autodétection/parsing des patches | Mode texte explicite et contrat d’indexabilité ; Git/UI vérifient que l’autre fichier reste intact. Résolu. |
