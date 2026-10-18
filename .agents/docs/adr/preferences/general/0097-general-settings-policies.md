# ADR 0097 · Réglages General et consommateurs

Statut : suites complètes, paquet exact et parcours natif source ciblé vérifiés ; audit étendu ouvert. Niveau N2 inchangé.

Retrouver les choix Windows dans General et leur effet réel au prochain usage. Réutiliser les groupes, clés XLF et dépendances : trois états des sous-modules, deux Follow indépendants, compteurs distincts. Les options console Windows et télémétrie restent désactivées et expliquées tant que leurs services Linux ne sont pas disponibles.

Une page est un brouillon, sans nouveau profil General. La session édite les autorités existantes de Pull, Process, Clone, sous-modules, historique fichier et Blame. Relire chaque profil avant publication et fusionner les seuls champs modifiés ; déclarer les erreurs et effets partiels, sans rollback fictif. Un retry conserve les intentions déjà publiées. Le défaut Clone et la dernière destination réussie sont distincts ; un défaut vide explicite reste vide.

Le plafond global Windows est indépendant du lot d’historique. Zéro signifie illimité ; Load More et présélection respectent le plafond choisi. Le délai de recherche est relu au prochain démarrage du timer. Réouvrir un dépôt récent est optionnel ; sa dernière visite ne dépend pas du tri ni des ancres de l’historique.

Désactiver le dirty-check checkout laisse un état inconnu, sans retirer les protections Git. La disponibilité des données est distincte de cet état : ne pas afficher les choix locaux avant la fin de l’initialisation. L’autostash est commun à Pull/checkout et distinct du stash manuel. Histogram partage une autorité avec Blame et atteint les vrais lecteurs, sans remplacer leurs autres options. Les statuts optionnels utilisent le tracking push avant upstream et les icônes de sous-modules Windows.

Repères : GeneralSettingsPage/Values/Session, HistoryLoadingPolicy, MainWindow.GeneralSettings, GlobalDiffPreferences, GitRepository.BrowseStatus ; détails d’exécution dans le TODO.
