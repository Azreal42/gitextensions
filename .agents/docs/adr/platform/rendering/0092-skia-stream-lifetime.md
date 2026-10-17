# ADR 0092 — Durée de vie du flux natif des fontes

Statut : backend intégré avec Fonts b74 ; full, paquet exact et natif applicatif borné vérifiés.

Choisir une police ou un style doit rester une opération ordinaire. Le catalogue et les styles ne sont pas réduits pour contourner un arrêt natif du moteur de texte.

Reconstruire le seul backend `Avalonia.Skia` 11.3.22 depuis les sources officielles exactes, avec le delta `using var asset` dans `GlyphTypefaceImpl.TryGetStream`. Le `SKStreamAsset` reste vivant pendant la copie native puis est libéré. La stratégie de lecture, les caches et les dépendances natives ne changent pas ; les autres assemblies Avalonia restent officielles.

Le backend local conserve l’identité, la surface publique, les ressources et les accès internes requis. Il est explicitement attribué comme fork ; sa signature publique ne vaut ni signature officielle du fournisseur ni approbation. Sources, manifeste de provenance, licence MIT et notice accompagnent le projet et le paquet. Toute mise à jour Avalonia doit réévaluer cette copie locale.

La [calibration bornée](../../../../../artifacts/linux/fonts-preferences-20261017/root-calibration12-review.json) et le scénario ordinaire comparent les octets d’une vraie fonte sous pression GC, dans des processus neufs avec attente et drainage réels. Ils ne certifient pas toutes les fontes, tous les styles ni les TTC ; résultats bruts et diagnostic sont conservés dans l’[historique](../../../failure.md#polices--densité-et-flux-natif).

Le [contrôle du backend livré](../../../../../artifacts/linux/snapshots/b74bb4522/proofs/package-backend-verification.json) couvre lock net8, références officielles de compilation, identité/surface/35 IVT, ressources, natives et licences. Le [full applicatif](../../../../../artifacts/linux/fonts-preferences-20261017/full-02/parent-verification.json), le [paquet exact](../../../../../artifacts/linux/snapshots/b74bb4522/proofs/package-parent-verification.json) et le [natif anglais/clair/X11](../../../../../artifacts/linux/snapshots/b74bb4522/proofs/native-fonts-review.json) ont des validations distinctes. Elles ne constituent pas une couverture universelle du moteur de texte ; limites et périmètre dans le [TODO](../../../todo.md#validation-courante--polices-et-eol). Aucun N4.
