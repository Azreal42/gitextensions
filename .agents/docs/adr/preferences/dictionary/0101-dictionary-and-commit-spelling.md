# ADR 0101 · Dictionnaire partagé et message Commit

Source `014c7e534` ; suites complètes, paquet isolé et natif source ciblé vérifiés.

Desktop choisit `en-US` par défaut ; `None` désactive la correction. Le XML local du répertoire Git commun prime sur le distribué de la racine, puis sur le profil global XDG. Commit écrit uniquement le local ; Appearance publie le global. Le format Windows conserve les autres clés ; lecture sans écriture et sauvegarde refusée si le snapshot est périmé, sans import global Windows.

Le moteur lie onze sources NetSpell et treize dictionnaires originaux. Chaque éditeur possède son moteur ; `NetSpell/user.dic`, par profil et commun aux langues, préserve les ressources installées. Catalogue absent, XML corrompu, identité inconnue et erreurs IO restent visibles.

Le TextBox natif conserve sélection et Undo : vérification après 250 ms, jusqu’à 4999 caractères, publication suspendue pendant l’IME et réponses périmées rejetées. Changer de dictionnaire retire les marques même à texte inchangé. La fermeture bloque les admissions et draine lectures, moteurs retirés et sauvegardes avant Closed.

Complétion Windows des noms/stems actuels et anciens, symboles, cinq préfixes fixes et exemptions NetSpell temporaires : raccord encore ouvert. Ignore conserve une occurrence au même texte, adaptation Linux explicite. [Full03 accepté](../../../../../artifacts/linux/dictionary-20261018/full-03/parent-verification.json) ; périmètres du paquet et de la GUI native dans le [TODO](../../../todo.md#dictionary--périmètre-vérifié).
