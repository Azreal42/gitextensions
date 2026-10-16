# ADR 0072 · Questions et notifications askpass

Statut : périmètre ciblé testé ; audit global ouvert.

Besoin : répondre aux invites Git/OpenSSH sans console, en distinguant saisie, confiance d’un hôte, autorisation d’une clé et simple notification. Fermer une question ne doit jamais autoriser implicitement l’opération.

Décision : conserver la disposition compacte Windows, son titre et les boutons OK/Cancel ou Yes/No ; prompt littéral sélectionnable, boutons traduits, thème partagé. Ajouter la saisie visible d’empreinte pour la confirmation d’hôte moderne. OpenSSH valide l’empreinte ; l’application ne modifie pas known_hosts.

| Invite | Réponse native |
| --- | --- |
| Identifiant / secret | Texte exact suivi de LF ; Cancel/fermeture échoue sans réponse |
| Question Yes/No | `yes` ou `no` sans LF ; Cancel/fermeture échoue |
| Confiance d’un hôte | Yes/No ou empreinte exacte sans LF ; Entrée dans un champ vide/invalide reste sans décision |
| Hint `confirm` | Yes réussit sans sortie ; No/Cancel échoue sans sortie |
| Hint `none` | Notification sans saisie ni autorisation ; fermeture sans réponse |

Les hints explicites priment sur le texte. Seul le nom d’utilisateur Git reconnu est démasqué ; mots de passe SSH, passphrases, PIN et invites inconnues restent masqués, même si un chemin contient `(yes/no)`. Aucun secret persisté, aucune trace du prompt/résultat. Sans argument, conserver l’invite Windows par défaut ; rejeter une arité invalide sans ouvrir de fenêtre.

Repères : `AskpassProtocol`, `AskpassApplication`, `AskpassWindow`, `gitextensions-askpass`. Le runner conserve la priorité des helpers configurés, les hints hérités et l’annulation des processus possédés.

Validation : AskpassProtocolTests, AskpassModesWindowTests, AskpassTests et AskpassModesScenarios ; suites, rendus et paquet rattachés à leur SHA dans le TODO. Les parcours X11 isolés du vrai helper ne valent pas audit Wayland, DPI/accessibilité, invites localisées ni matériel FIDO réel. Les nouveaux libellés d’empreinte utilisent le repli anglais du catalogue ; aucun N4.
