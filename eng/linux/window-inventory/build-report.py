#!/usr/bin/env python3
"""Join a Roslyn census and explicit per-screen reviews; reject unclassified changes."""
import argparse
import collections
import csv
import hashlib
import io
import json
import re
import subprocess
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[2]
DOCS = ROOT / '.agents/docs'
PARSER = argparse.ArgumentParser(description=__doc__)
PARSER.add_argument('--syntax', required=True, type=Path)
PARSER.add_argument('--map', type=Path, default=HERE/'port-map.json', help='Explicit review map (default: adjacent port-map.json)')
PARSER.add_argument('--check', action='store_true', help='Check generated files without writing them')
ARGS = PARSER.parse_args()
RAW = json.loads(ARGS.syntax.read_text())
MAP = json.loads(ARGS.map.read_text())
if RAW['diagnostics']:
    raise SystemExit('Roslyn syntax errors: ' + str(RAW['diagnostics'][:5]))
TYPES = {}
for t in RAW['types']:
    v = TYPES.setdefault(t['id'], dict(id=t['id'], name=t['name'], bases=set(), sources=set(), methods=set(), members=set(), fields=set(), abstract=False))
    v['bases'].update(t['bases']); v['sources'].add((t['path'], t['line'])); v['methods'].update(t['methods'])
    v['members'].update((t['path'], x['line'], x['name']) for x in t['members'])
    v['fields'].update((x['name'], x['type']) for x in t['fields'])
    v['abstract'] |= t['abstract']
BY_NAME = collections.defaultdict(list)
for t in TYPES.values(): BY_NAME[t['name']].append(t['id'])
def simple(s): return s.replace('global::', '').split('<')[0].split('.')[-1].strip()
def resolve(base, owner):
    b = base.replace('global::', '').split('<')[0]
    if b in TYPES: return b
    parts = owner.split('.')[:-1]
    while parts:
        q = '.'.join(parts) + '.' + b
        if q in TYPES: return q
        parts.pop()
    matches = BY_NAME.get(simple(base), [])
    return matches[0] if len(matches) == 1 else None

def inherits(t, roots, seen=frozenset()):
    if t['id'] in seen: return False
    for b in t['bases']:
        if b in roots: return True
        key = resolve(b, t['id'])
        if key and inherits(TYPES[key], roots, seen | {t['id']}): return True
    return False

def source(t):
    # Prefer the actual declaration over a partial Designer or MainWindow split.
    declared = [(p,l) for p,l in t['sources'] if not p.endswith('.Designer.cs') and t['bases'] and re.search(r'\bclass\s+'+re.escape(t['name'])+r'(?:<[^>]*>)?\s*:', (ROOT/p).read_text(encoding='utf-8-sig'))]
    return min(declared or t['sources'], key=lambda x:(x[0].endswith('.Designer.cs'), x[0], x[1]))

def plain(t):
    p,l = source(t)
    return dict(id=t['id'],name=t['name'],source=p,line=l,sources=[dict(path=p,line=l) for p,l in sorted(t['sources'])],bases=sorted(t['bases']),abstract=t['abstract'])

def is_windows(t): return any(p.startswith(('src/app/','src/plugins/')) for p,l in t['sources'])
def is_linux(t): return any(p.startswith('src/linux/') for p,l in t['sources'])
WINDOWS = [t for t in TYPES.values() if is_windows(t) and inherits(t, {'Form', 'System.Windows.Forms.Form'})]
LINUX = [t for t in TYPES.values() if is_linux(t) and inherits(t, {'Window', 'Avalonia.Controls.Window'})]
SETTINGS = [t for t in TYPES.values() if is_windows(t) and (inherits(t, {'SettingsPageBase'}) or any(simple(b)=='IBuildServerSettingsUserControl' for b in t['bases']))]
ACTUAL = {t['id'] for t in WINDOWS + SETTINGS}
if ACTUAL != MAP['entries'].keys():
    raise SystemExit('Unreviewed inventory change: missing='+str(sorted(ACTUAL-MAP['entries'].keys()))+'; obsolete='+str(sorted(MAP['entries'].keys()-ACTUAL)))

def target(token):
    if token.startswith('file:'):
        path = token[5:]
        assert (ROOT/path).is_file(), token
        return dict(label=Path(path).name,source=path,line=1,kind='source')
    name,sep,method = token.partition('@')
    options = [TYPES[k] for k in BY_NAME[name] if is_linux(TYPES[k])]
    assert len(options)==1, ('Invalid/ambiguous Linux target',token)
    t = options[0]
    if sep:
        matches = sorted((p,l) for p,l,m in t['members'] if m==method)
        assert matches, ('Missing Linux method',token)
        p,l = matches[0]
    else: p,l=source(t)
    return dict(id=t['id'],label=name+('.'+method if sep else ''),source=p,line=l,
                kind='window' if t in LINUX else 'component')

REFERENCE=json.loads((HERE/'validation-reference.json').read_text())
TRX=ROOT/REFERENCE['trx']
if TRX.exists(): assert hashlib.sha256(TRX.read_bytes()).hexdigest()==REFERENCE['trx_sha256'], 'Reference TRX changed'
assert REFERENCE['counters']['passed']==REFERENCE['counters']['total']=='874' and REFERENCE['counters']['failed']=='0'
EXECUTED={name:[outcome for outcome,count in counts.items() for _ in range(count)] for name,counts in REFERENCE['classes'].items()}
VALIDATION_CODE_EQUIVALENT=(subprocess.check_output(['git','diff',REFERENCE['source_sha'],'--','src/linux','tests/linux'],cwd=ROOT)==b''
    and subprocess.check_output(['git','ls-files','--others','--exclude-standard','--','src/linux','tests/linux'],cwd=ROOT)==b'')

def evidence(t, review, targets):
    names=set(review.get('tests',[]))
    # Only a direct dedicated-window type reference is an automatic candidate.
    # MainWindow and general factories would misleadingly associate most tests.
    dedicated=[x['label'] for x in targets if x['kind']=='window' and x['label'] not in ('MainWindow','GitProcessDialog','DesktopInformationWindow','PluginDialog')]
    for p in RAW['files']:
        if not p.startswith('tests/linux/GitExtensions.Desktop.Tests/') or not p.endswith('Tests.cs'): continue
        text=(ROOT/p).read_text(encoding='utf-8-sig')
        if any(re.search(r'\b'+re.escape(name)+r'\b',text) for name in dedicated): names.add(Path(p).stem)
    out=[]
    for name in sorted(names):
        paths=[p for p in RAW['files'] if p.startswith('tests/linux/') and Path(p).stem==name]
        assert len(paths)==1, ('Missing test evidence',t['name'],name)
        statuses=EXECUTED.get(name,[])
        out.append(dict(source=paths[0],test_class=name,passed_in_reference_run=statuses.count('Passed'),results_present=bool(statuses)))
    return out
ADR={}
for p in (DOCS/'adr').rglob('*.md'):
    number=p.name.split('-')[0]
    ADR.setdefault(number,[]).append(str(p.relative_to(ROOT)))
rows=[]
for t in sorted(WINDOWS+SETTINGS,key=lambda t:t['id']):
    review=MAP['entries'][t['id']]
    assert review['level'] in MAP['levels'],t['id']
    targets=[target(x) for x in review['linux']]
    tests=evidence(t,review,targets)
    if review['level']=='N3': assert targets and any(x['results_present'] for x in tests), ('N3 without executed targeted evidence',t['name'])
    adr=[]
    for number in review.get('adr',[]):
        assert len(ADR.get(number,[]))==1,('Unknown ADR',number)
        adr+=ADR[number]
    for ref in review.get('extra_evidence',[]):
        assert (ROOT/ref['source']).is_file() and 1<=ref['line']<=len((ROOT/ref['source']).read_text(encoding='utf-8-sig').splitlines()),ref
    rows.append(dict(**plain(t),category='settings' if t in SETTINGS else 'windows',**{k:v for k,v in review.items() if k not in ('linux','tests','adr')},linux=targets,tests=tests,adr=adr))
# C++ askpass is an actual source-owned dialog, outside the C# inheritance tree.
NATIVE=[]
for path in sorted(ROOT.glob('src/native/**/*.rc')):
    for i,line in enumerate(path.read_text(encoding='utf-8-sig',errors='strict').splitlines(),1):
        m=re.match(r'\s*(\w+)\s+DIALOG(?:EX)?\s',line)
        if m:
            assert m.group(1)=='IDD_ASK_PASSWORD', ('New native dialog needs review',path,i)
            NATIVE.append(dict(id='native:'+m.group(1),name=m.group(1),source=str(path.relative_to(ROOT)),line=i,category='native',feature='Saisie SSH askpass',group='platform',level='N3',linux=[target('AskpassWindow')],implemented='Invite Git/SSH éphémère, mot de passe/identifiant, sans stockage de secret.',remaining='Comparer toutes les invites et modes d’échec sur les bureaux Linux supportés.',visual='adapte',tests=[dict(source='tests/linux/GitExtensions.Desktop.Tests/AskpassTests.cs',test_class='AskpassTests',passed_in_reference_run=EXECUTED.get('AskpassTests',[]).count('Passed'),results_present=bool(EXECUTED.get('AskpassTests')))],adr=ADR['0006']))
assert len(NATIVE)==1, 'Expected the separately reviewed native askpass resource'
rows += NATIVE
# Reverse view: account for every Linux Window, including helper-only additions.
EXTRAS={
 'AskpassWindow':'Équivalent de la ressource C++ native SSH askpass.',
 'NoteDialog':'Édition de notes Git ; fonction de l’éditeur/menus Windows, sans FormNote dédié.',
 'CommitEmptyDialog':'Choix de commit sans index ; variante de confirmation de FormCommit.',
 'NativeRebaseEditorWindow':'Éditeur natif des messages de rebase ; spécialisation, pas un éditeur FormEditor générique.',
 'PatchConflictDialog':'Reprise d’apply/am ; auxiliaire de FormApplyPatch.',
 'CheckoutSubmodulePrompt':'Confirmation après checkout ; auxiliaire de FormCheckoutBranch.',
 'StashDropConfirmation':'Confirmation de suppression ; auxiliaire de FormStash.',
 'SubmoduleRemovalDialog':'Retrait sûr ; sous-workflow de FormSubmodules.',
 'SubmoduleRemovalConfirmation':'Confirmation de retrait ; sous-workflow de FormSubmodules.',
 'SubmoduleUpdateDialog':'Mise à jour des sous-modules ; sous-workflow de FormSubmodules/Checkout/Pull.',
 'UndoLastCommitConfirmationDialog':'Confirmation Undo ; variante du menu de FormBrowse.',
 'RepositoryDialog':'Base abstraite des fenêtres liées à un dépôt.',
 'StartupRepositoryDialog':'Base abstraite de Clone/Init.',
}
reverse=[]
for t in sorted(LINUX,key=lambda t:t['id']):
    owners=[dict(id=r['id'],name=r['name'],level=r['level']) for r in rows if any(x.get('id')==t['id'] for x in r['linux'])]
    assert owners or t['name'] in EXTRAS,('Unaccounted Linux Window',t['id'])
    reverse.append(dict(**plain(t),windows=owners,note=EXTRAS.get(t['name'],'')))
# Dynamic windows are not omitted just because they have no dedicated class.
OBJECTS=list({(o['path'],o['line']):o for o in RAW['objects']}.values())
DYNAMIC=[]
for o in sorted(OBJECTS,key=lambda o:(o['path'],o['line'])):
    if o['path'].startswith(('src/app/','src/plugins/')) and o['type'] in ('Form','System.Windows.Forms.Form'):
        DYNAMIC.append(dict(**{k:v for k,v in o.items() if k!='mode'},platform='windows',role='Hôte technique de démarrage/erreur ; pas de fenêtre métier supplémentaire.'))
    if o['path'].startswith('src/linux/') and o['type'] in ('Window','Avalonia.Controls.Window'):
        DYNAMIC.append(dict(**{k:v for k,v in o.items() if k!='mode'},platform='linux',role='Fenêtre créée dans cette méthode ; voir les usages/équivalences de son propriétaire.'))
SYSTEM=[]
COMMON={'OpenFileDialog','SaveFileDialog','FolderBrowserDialog','FontDialog','ColorDialog','TaskDialog'}
for o in OBJECTS:
    if o['path'].startswith(('src/app/','src/plugins/')) and simple(o['type'] or '') in COMMON:
        SYSTEM.append(dict(kind=simple(o['type']),source=o['path'],line=o['line'],owner=o['owner'],method=o['method'],evidence='construction'))
CALLS=list({(o['path'],o['line'],o['expression']):o for o in RAW['calls']}.values())
for call in CALLS:
    if call['path'].startswith(('src/app/','src/plugins/')) and re.search(r'(?:^|\.)(?:MessageBoxes\.|MessageBox\.Show|TaskDialog\.ShowDialog)', call['expression']):
        SYSTEM.append(dict(kind='MessageBox/TaskDialog',source=call['path'],line=call['line'],owner=call['owner'],method=call['method'],evidence=call['expression']))
SYSTEM.sort(key=lambda x:(x['source'],x['line'],x['evidence']))
DESIGNERS=[]
for path in RAW['files']:
    if not path.startswith(('src/app/','src/plugins/')) or not path.endswith('.Designer.cs'):continue
    owners=[t for t in TYPES.values() if any(p==path for p,l in t['sources'])]
    assert owners, ('Unparsed Designer file',path)
    for t in owners:
        kind='window' if t in WINDOWS else 'settings' if t in SETTINGS else 'control/resource'
        DESIGNERS.append(dict(source=path,type=t['id'],kind=kind))
WINDOW_COUNT=collections.Counter(r['level'] for r in rows if r['category'] in ('windows','native') and r['level']!='BASE')
PAGE_COUNT=collections.Counter(r['level'] for r in rows if r['category']=='settings' and r['level']!='BASE')
source_paths=sorted(p for p in RAW['files'])
digest=hashlib.sha256()
for path in source_paths:
    digest.update(path.encode()+b'\0'+hashlib.sha256((ROOT/path).read_bytes()).digest())
anchor=subprocess.check_output(['git','log','-1','--format=%H','--','src/app','src/plugins','src/linux','tests/linux'],cwd=ROOT,text=True).strip()
report=dict(schema_version=1,code_reference_commit=anchor,source_digest_sha256=digest.hexdigest(),scanned_csharp_files=len(source_paths),parsed_configurations=['Windows release','Windows debug','Linux release','Linux debug'],levels=MAP['levels'],counts=dict(windows_form_types=len(WINDOWS),native_dialog_resources=len(NATIVE),windows_business_dialogs=sum(WINDOW_COUNT.values()),windows_support_classes=sum(r['level']=='BASE' for r in rows if r['category']=='windows'),settings_types=len(SETTINGS),settings_pages=sum(PAGE_COUNT.values()),settings_support_classes=sum(r['level']=='BASE' for r in rows if r['category']=='settings'),linux_window_types=len(LINUX),linux_abstract_bases=sum(t['abstract'] for t in LINUX),linux_dynamic_factories=sum(x['platform']=='linux' for x in DYNAMIC),windows_dynamic_hosts=sum(x['platform']=='windows' for x in DYNAMIC),windows_levels=dict(sorted(WINDOW_COUNT.items())),settings_levels=dict(sorted(PAGE_COUNT.items())),system_dialog_sites=len(SYSTEM),designer_files=len({x['source'] for x in DESIGNERS})),validation_reference=dict(trx=str(TRX.relative_to(ROOT)),source_sha=REFERENCE['source_sha'],trx_sha256=REFERENCE['trx_sha256'],reference_snapshot='eng/linux/window-inventory/validation-reference.json',additional_suites=REFERENCE.get('additional_suites',{}),current_linux_code_equivalent=VALIDATION_CODE_EQUIVALENT,meaning='Tests de la source de référence seulement ; un test associé ne certifie pas tous les comportements de sa fenêtre. Aucun test applicatif réexécuté pour ce recensement documentaire.'),screens=rows,linux_reverse=reverse,dynamic_factories=DYNAMIC,system_dialog_sites=SYSTEM,designer_reconciliation=DESIGNERS)

def link(label,path,line=None): return '['+label.replace('|','/')+'](../../'+path+('#L'+str(line) if line else '')+')'
def esc(s):return str(s).replace('|','/').replace('\n',' ')
def csv_text(fields,values):
    text=io.StringIO(newline='');writer=csv.DictWriter(text,fieldnames=fields,lineterminator="\n");writer.writeheader();writer.writerows(values);return text.getvalue()
def md():
    out=['# Inventaire Windows → Linux et niveau de portage','',f"Référence du code : `{anchor}`. Recensement du 16 octobre 2026 ; {len(source_paths)} fichiers C# du checkout analysés avec Roslyn (suivis et non ignorés), configurations Release et Debug des sources Windows et Linux. Aucune syntaxe en erreur.",'',
    'Le périmètre couvre les classes de fenêtres WinForms de `src/app` et `src/plugins`, leurs bases héritées et parties Designer, la ressource C++ askpass, les pages de paramètres et les constructions de fenêtres sans classe dédiée. Les boîtes système sont recensées par site dans le CSV annexe. Les exécutables externes, les écrans générés par l’installateur WiX, les tests/démos et les popups internes de bibliothèques tierces ne sont pas des fenêtres appartenant à ce code ; ils ne sont pas comptés comme portés.', '',
    '**Recensement exhaustif des types et ressources de fenêtres de ce périmètre ; évaluation par écran, pas audit de chaque bouton ni de toutes les valeurs possibles d’un dialogue.** Une adaptation dans Browse ou un prompt générique n’est pas comptée comme une fenêtre Windows intégralement portée. `N3` ne signifie pas 100 % et aucune entrée n’est `N4`. Les 13 familles de migration du TODO restent ouvertes.','',
    '## Bilan','', '| Périmètre | Total | N0 absent | N1 minimal | N2 partiel | N3 ciblé testé | N4 iso audité | Spécifique Windows |','| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |']
    for label,counter in [('Fenêtres/dialogues métier, C# et askpass C++',WINDOW_COUNT),('Pages de paramètres, y compris CI',PAGE_COUNT)]:out.append('| '+label+' | '+str(sum(counter.values()))+' | '+' | '.join(str(counter[x]) for x in ('N0','N1','N2','N3','N4','NA'))+' |')
    out+=['',f"À part : **{len(WINDOWS)} types WinForms** dont {report['counts']['windows_support_classes']} bases de support ; **{len(LINUX)} types Window Avalonia** dont {report['counts']['linux_abstract_bases']} bases abstraites, et **{report['counts']['linux_dynamic_factories']} fabriques de fenêtres Linux**. Les {report['counts']['settings_support_classes']} bases de pages de paramètres et {report['counts']['windows_dynamic_hosts']} hôtes WinForms techniques sont exclus des comptes métier.",'',
    'Ces comptes mesurent des écrans, pas une part du travail : une boîte de titre et Browse pèsent chacun une ligne. Aucun pourcentage global de completion n’est déduit de ces nombres.','',
    '## Lecture des niveaux','']
    out += ['- **'+key+'** — '+value+'.' for key,value in MAP['levels'].items()]
    out+=['','Aspect : `rapproche` = disposition/éléments Windows repris sur un périmètre ; `adapte` = interface native différente ; `integre` = fonction dans un autre écran/menu ; `generique` = prompt/UI commune ; `externe` = CLI/navigateur/éditeur ; `systeme` = dialogue du bureau ou adaptation OS. Aucune de ces mentions n’atteste une parité visuelle intégrale.','',
    'Validation : les liens de tests signalent des scénarios associés présents dans la suite de référence **874/874 Desktop**, source `879f7bae3`. Ils ne valent pas audit de toute la fenêtre. Les scénarios Plugins explicitement liés sont rattachés au lot existant de 35/35 ; un test du moteur ne valide pas son interface. Les ADR précisent les périmètres natifs acceptés et encore ouverts ; les preuves anciennes ne sont pas transférées à une nouvelle source. Les dialogues recensés `N0` restent absents même si leur moteur ou un contrôle voisin existe.','',
    '## Écarts prioritaires révélés par le relevé','',
    '- Paramètres : pages absentes ou dispersées, notamment raccourcis configurables, scripts/événements, langue, fontes, outils et intégrations CI.','- Remotes, worktrees et ajout de sous-module : workflows ou prompts simplifiés, fenêtres Windows complètes non reprises.','- Éditeurs `.gitignore`, `.gitattributes`, `.mailmap`, assistant de conflit de sous-module et fenêtres de PR/hébergement absents.','- Journal Git partiel : métadonnées disponibles, sorties/cache/stacks/export Windows absents.','- Menus View/Navigate : le périmètre récent est testé, mais recherche incrémentale et sélection interactive des parents/enfants restent ouverts.','',
    '## Fenêtres Windows et équivalents Linux','']
    groups={'navigation':'Navigation et historique','repositories':'Dépôts, worktrees et sous-modules','references':'Branches, tags et références','operations':'Commit et opérations sur l’historique','network':'Remotes et réseau','files':'Fichiers, lecteurs et exports','maintenance':'Maintenance','helpers':'Sélecteurs, prompts et processus','settings':'Fenêtres des paramètres','hosting':'Hébergement et CI','information':'Aide, diagnostic et informations','platform':'Adaptations de plateforme','plugins':'Fenêtres des plugins'}
    for group,label in groups.items():
        subset=[r for r in rows if r['category'] in ('windows','native') and r['level']!='BASE' and r['group']==group]
        if not subset:continue
        out+=['### '+label,'','| Fenêtre Windows / usage | Équivalent Linux | Niveau / aspect | Présent et écarts restants | Preuves |','| --- | --- | --- | --- | --- |']
        out += [row_md(r) for r in sorted(subset,key=lambda r:r['name'])]
        out.append('')
    out+=['## Chaque page de paramètres Windows','', '| Page Windows / usage | Équivalent Linux | Niveau / aspect | Présent et écarts restants | Preuves |','| --- | --- | --- | --- | --- |']
    out += [row_md(r) for r in rows if r['category']=='settings' and r['level']!='BASE']
    out+=['','## Classes de support exclues des comptes métier','','| Base Windows | Équivalent / rôle |','| --- | --- |']
    for r in rows:
        if r['level']=='BASE':out.append('| '+link(r['name'],r['source'],r['line'])+' | '+('; '.join(link(x['label'],x['source'],x['line']) for x in r['linux']) or 'Support commun sans fenêtre utilisateur autonome')+' |')
    out+=['','## Inventaire inverse de toutes les fenêtres Avalonia','','Cette vue empêche de perdre les variantes Linux et les confirmations sans classe Windows équivalente. Le lien indique le type réel, pas une égalité fonctionnelle automatique.','','| Fenêtre Linux | Windows associé / rôle |','| --- | --- |']
    for r in reverse:
        owners=', '.join(x['name']+' ('+x['level']+')' for x in r['windows'])
        out.append('| '+link(r['name'],r['source'],r['line'])+' | '+esc('; '.join(x for x in (owners,r['note']) if x))+' |')
    out+=['','## Fenêtres créées par une méthode, sans classe dédiée','','Les noms de méthodes distinguent les variantes ; les confirmations de Pull ne sont pas un type de fenêtre unique implicitement « terminé ».','','| Plateforme / propriétaire | Fabrique | Rôle |','| --- | --- | --- |']
    for r in DYNAMIC:out.append('| '+r['platform']+' / '+esc((r['owner'] or '').split('.')[-1])+' | '+link(r['method'],r['path'],r['line'])+' | '+esc(r['role'])+' |')
    out+=['','## Boîtes système et confirmations Windows','','| Famille | Équivalent Linux | État |','| --- | --- | --- |',
    '| OpenFileDialog | StorageProvider.OpenFilePickerAsync selon le workflow | Adaptation OS ; tous les sites Windows ne sont pas repris. |',
    '| SaveFileDialog | StorageProvider.SaveFilePickerAsync selon le workflow | Adaptation OS ; journal/cache et certains exports restent absents. |',
    '| FolderBrowserDialog | StorageProvider.OpenFolderPickerAsync | Utilisé pour ouverture/clone/cleanup ; autres sites à comparer. |',
    '| FontDialog | Aucun sélecteur global équivalent | Non porté ; le zoom d’un lecteur ne remplace pas le choix de polices. |',
    '| ColorDialog | Thèmes/palettes et réglages partiels | Éditeur de couleurs complet absent. |',
    '| MessageBox / TaskDialog | Dialogs.ConfirmAsync, ShowErrorAsync, fenêtres spécialisées | Adaptations par workflow ; aucune certification globale de tous les boutons/choix. |','',
    f"Le [CSV des {len(SYSTEM)} sites système/confirmations](windows-linux-system-dialog-sites.csv) donne chaque fichier, ligne, propriétaire et méthode, avec la ligne de la matrice correspondante lorsque disponible. **Ces sites ne sont pas individuellement déclarés portés.**",'',
    '## Sources et mise à jour','',
    '- [Matrice structurée complète](windows-linux-window-inventory.json), incluant les deux sens, les fabriques dynamiques et la réconciliation des Designer.','- [CSV des fenêtres/pages](windows-linux-window-inventory.csv), pour filtrer par niveau ou famille.','- [Évaluations explicites et écarts](../../eng/linux/window-inventory/port-map.json).','- [Générateur et commande de contrôle](../../eng/linux/window-inventory/README.md).','- [Suivi de migration](todo.md).','',
    f"Réconciliation : {report['counts']['designer_files']} fichiers Designer classés (fenêtre, page, contrôle/ressource), héritage transitif, classes partielles fusionnées, ressource askpass C++ ajoutée. Le générateur échoue si un nouveau type Windows/page n’a pas d’évaluation ou si un équivalent Linux/membre référencé disparaît. Le digest des {len(source_paths)} fichiers et la référence Git sont dans le JSON.", '']
    return '\n'.join(out)

def row_md(r):
    original=link(r['name'],r['source'],r['line'])+'<br>'+esc(r['feature'])
    equivalent='<br>'.join(link(x['label'],x['source'],x['line']) for x in r['linux']) or '—'
    proof=[]
    executed=[x for x in r['tests'] if x['results_present']]
    if executed:
        proof += [link(x['test_class'],x['source']) for x in executed[:2]]
        if len(executed)>2:proof.append('+'+str(len(executed)-2)+' classes (JSON)')
    elif r['tests']:proof.append('Tests source associés ; exécution non rattachée')
    else:proof.append('Code seul / absence constatée')
    for ref in r.get('extra_evidence',[]): proof.append(link(ref['label'],ref['source'],ref['line']))
    if r['adr']: proof.append('ADR '+', '.join(link(Path(p).name[:4],p) for p in r['adr']))
    return '| '+original+' | '+equivalent+' | **'+r['level']+'** / '+r['visual']+' | '+esc(r['implemented'])+'<br>**Reste :** '+esc(r['remaining'])+' | '+'<br>'.join(proof)+' |'

csv_rows=[]
for r in rows:
    csv_rows.append(dict(id=r['id'],category=r['category'],feature=r['feature'],group=r['group'],level=r['level'],visual=r['visual'],windows_source=r['source']+':'+str(r['line']),linux='; '.join(x['label']+' @ '+x['source']+':'+str(x['line']) for x in r['linux']),implemented=r['implemented'],remaining=r['remaining'],tests='; '.join(x['test_class'] for x in r['tests']),adr='; '.join(r['adr']),extra_evidence='; '.join(x['label']+' @ '+x['source']+':'+str(x['line']) for x in r.get('extra_evidence',[]))))
system_rows=[]
for r in SYSTEM:
    review=MAP['entries'].get(r['owner'],{})
    system_rows.append(dict(**r,owner_level=review.get('level','CONTROL/HELPER'),site_status='NON_AUDITE_INDIVIDUELLEMENT',owner_linux='; '.join(review.get('linux',[]))))
outputs={
 'windows-linux-window-inventory.md':md(),
 'windows-linux-window-inventory.json':json.dumps(report,ensure_ascii=False,indent=2)+'\n',
 'windows-linux-window-inventory.csv':csv_text(list(csv_rows[0]),csv_rows),
 'windows-linux-system-dialog-sites.csv':csv_text(list(system_rows[0]),system_rows),
}
for name,value in outputs.items():
    destination=DOCS/name
    if ARGS.check:
        assert destination.is_file() and destination.read_text()==value, 'Stale generated report: '+str(destination)
    else:destination.write_text(value)
print(json.dumps(dict(status='CHECKED' if ARGS.check else 'GENERATED',**report['counts']),ensure_ascii=False))
