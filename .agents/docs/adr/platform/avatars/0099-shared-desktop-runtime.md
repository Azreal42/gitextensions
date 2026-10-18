# ADR 0099 · Avatars partagés entre fenêtres

Statut : socle intégré, validation globale en cours ; [ciblé06 252/252](../../../../../artifacts/linux/appearance-settings-20261018/review/avatar-targeted-06-green.json), dont 103 nouveaux cas. Ne remplace pas le prochain full, paquet ou parcours natif.

Browse, CommitInfo, Blame et Gource doivent présenter la même politique d’avatars. Les enums et TemplateFormatter Windows sont réutilisés ; None/initiales reste le défaut sans réseau. Un runtime appartient à la vraie App après construction de la fenêtre initiale, indépendamment du nombre de lecteurs encore ouverts. Helpers et builder headless restent dormants. La durée disque est capturée à sa création comme Windows ; changer les jours ne la recapture pas. Cache XDG versionné, pas d’import du cache Windows.

Apply publie uniquement les valeurs effectivement sauvegardées, même si un profil suivant échoue ; Cancel abandonne le brouillon restant. Vider le cache agit immédiatement sur la politique acquise sans sauvegarder le brouillon. Fermer Settings annule son attente, pas cette mutation partagée. Images immuables, générations et scopes empêchent une réponse retirée de repeupler le cache ; chaque fenêtre draine ses vrais workers avant Closed. Program draine le runtime après retour effectif de la boucle UI, sans nouveau dispatch.

Repères : `Avatars/AvatarService`, `AvatarProviderProtocol/Chain`, `AvatarMemoryCache/LocalCache`, `AvatarWindowLifetime`, `Controls/AvatarImageControl`, `MainWindow.Avatars`, `App/Program` ; publication dans `AppearanceSettingsSession` et `MainWindow.ToolsHelp`. Le provider Gource explicite injectable conserve son parcours historique (ADR0013). Tests `Avatar*Tests` ; lifecycle App/classic, autres variantes natives, DPI et macOS restent à vérifier.
