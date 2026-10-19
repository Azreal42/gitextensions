# ADR 0118 — Checkout d'une révision

Ouvrir le formulaire Windows compact depuis Checkout this commit : révision modifiable, picker partagé, SHA court/compteur et Force explicite pour cette tentative.

Un seul workflow encadre la vraie commande par BeforeCheckout et AfterCheckout. Le hook After suit un processus terminé, même nonzero ; veto, lancement échoué et annulation n'inventent pas ce hook. Les sous-modules ne sont proposés qu'après succès et changement du HEAD réellement relu. Une fermeture propriétaire annule et draine les enfants.

Sur erreur Git, Linux conserve le formulaire pour corriger la cible ou réessayer avec Force ; Windows le ferme après FormProcess. La saisie se résout à l'initialisation, au choix et à l'action plutôt qu'à Leave. Repères : `CheckoutRevisionDialog`, `MainWindow.CheckoutRevisionWithScriptEventsAsync`, `GitRepository.CheckoutRevisionAttemptAsync`.
