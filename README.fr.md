# SharpPyxis.SqlServer.SchemaMcp

[English](README.md) · Français

Serveur MCP local, en lecture seule, qui permet à un agent d'IA de lire la structure d'une base SQL
Server — objets, code, dépendances, colonnes — sans jamais en lire les données.

*Les scripts et les exemples de code sont en anglais, comme le reste du dépôt.*

## L'idée

Comment laisser quelqu'un travailler sur une base de données sans lui montrer les données qu'elle
contient ? La question est aussi ancienne que les bases de données, et SQL Server y répond par les
droits.

Un login qui ne détient que le droit `VIEW DEFINITION` voit la structure d'une base. Il voit la liste
des tables et leurs colonnes. Il lit le texte des vues, des procédures stockées et des fonctions. Il
voit les index et les clés étrangères. En revanche, il ne peut lire aucune ligne d'aucune table. Et il
ne peut rien modifier.

Ce serveur MCP applique la même réponse à un agent d'IA. L'agent interroge la base par
l'intermédiaire du serveur MCP. Le serveur MCP se connecte à l'instance SQL Server avec un login qui ne
détient que ce droit. L'agent connaît donc la structure de la base, et rien de plus.

Pour un développeur, le bénéfice est immédiat. Plus besoin de coller dans la conversation le résultat
d'une requête sur le catalogue pour que l'agent sache comment la base est construite. L'agent consulte
lui-même les noms exacts des tables et des colonnes, leurs types, les procédures qui utilisent une
table. Le SQL qu'il propose repose sur la base réelle, et non sur ce qu'il suppose.

Pour un DBA ou un responsable informatique, la question est autre : que peut faire cet agent sur la
base, et comment le vérifier ? La partie *Ce qui garantit que l'agent ne lit pas les données*, plus
bas, y répond.

## Où il s'exécute

Le serveur MCP est un programme qui s'exécute sur votre propre ordinateur, à côté de l'application
d'IA qui l'utilise. Cette application le lance quand elle en a besoin, et dialogue avec lui par
l'entrée et la sortie standard du programme. La norme MCP appelle cela un serveur local.

Trois conséquences en découlent.

L'application d'IA doit être installée sur le même ordinateur, et elle doit prendre en charge les
serveurs MCP locaux. Ce serveur MCP est testé avec Claude Desktop, et vérifié avec Claude Code et avec
GitHub Copilot dans VS Code. Tout client MCP capable de lancer un serveur local convient aussi. Un
chat ouvert dans un navigateur web s'exécute sur les serveurs de son fournisseur : il ne peut pas
lancer un programme sur votre ordinateur, et ne peut donc pas utiliser ce serveur MCP, quel que soit
son fournisseur.

L'ordinateur doit fonctionner sous Windows. Le serveur MCP conserve vos connexions dans un fichier
chiffré avec DPAPI, un mécanisme de chiffrement de Windows (voir *Les connexions*, plus bas).

L'ordinateur doit atteindre l'instance SQL Server par le réseau, comme le ferait SSMS. Le serveur MCP
n'ouvre aucun port réseau : sa seule connexion est celle qu'il établit vers l'instance, avec le login
que vous lui donnez.

Tout ce que renvoie le serveur MCP entre dans la conversation. Il est donc envoyé au fournisseur du
modèle d'IA, comme le reste de ce que vous tapez. Ce qu'il renvoie est la structure de la base, jamais
ses données : la partie suivante explique ce qui le garantit.

## Ce qui garantit que l'agent ne lit pas les données

### Deux protections, dont une seule fait la garantie

La première protection est le code du serveur MCP. Il n'exécute que des requêtes écrites à l'avance,
qui lisent le catalogue de SQL Server, c'est-à-dire les vues système qui décrivent les objets de la
base. Ni l'utilisateur ni l'agent d'IA ne peuvent modifier ces requêtes. Les outils du serveur MCP
prennent des paramètres, comme un nom de schéma ou une partie d'un nom d'objet. Ils ne prennent jamais
de SQL. Ce code est public : vous pouvez le lire dans ce dépôt.

La seconde protection est celle qui fait la garantie : les droits du login. Même si le code du serveur
MCP avait un défaut, ou avait été modifié, il ne pourrait pas faire plus que ce que son login permet.
Et ces droits, c'est vous qui les accordez, avec un script que vous lisez avant de l'exécuter. Vous
n'avez donc pas à faire confiance au serveur MCP. Vous pouvez vérifier vous-même, sur votre propre
instance, ce que son login peut faire et ce qu'il ne peut pas faire.

### Le seul chiffre qui touche aux données : le nombre de lignes

Les outils `list_objects` et `describe_object` donnent le nombre approximatif de lignes de chaque
table. À première vue, on dirait que le serveur MCP a lu les tables. Ce n'est pas le cas.

SQL Server conserve ce nombre dans son catalogue, dans la vue système `sys.partitions`, parmi ce qu'il
sait du stockage d'une table. Le droit `VIEW DEFINITION` le rend visible, exactement comme SSMS
l'affiche dans les propriétés d'une table. Le serveur MCP lit ce nombre, et rien d'autre : il ne compte
jamais les lignes, et ne les lit jamais. Le nombre est approximatif parce que SQL Server l'entretient
pour ses propres besoins, et non comme un décompte exact.

### Les quatre étapes

La mise en place se fait en quatre étapes. Les trois scripts qu'elles utilisent sont dans le dossier
`db/` de ce dépôt, et chacun est commenté en détail.

1. Vérifier que l'instance SQL Server accepte l'authentification SQL Server.
2. Créer le login, avec `Create-DdlReaderLogin.sql`.
3. Vérifier ce que ce login peut faire, avec `Verify-DdlReaderRights.sql`.
4. Relever ce que la base accorde à tous ses utilisateurs, avec `Review-PublicPermissions.sql`.

#### Étape 1 — L'instance accepte-t-elle l'authentification SQL Server ?

Le login créé à l'étape 2 est un login SQL Server : un nom et un mot de passe, gérés par l'instance
elle-même. Or une instance peut être configurée pour n'accepter que l'authentification Windows. Dans
ce cas, la création du login réussit, mais toute connexion avec lui est ensuite refusée (erreur 18456).

Pour le savoir, exécutez :

```sql
select serverproperty('IsIntegratedSecurityOnly') as windows_only;
```

Un résultat de 1 signifie que l'instance n'accepte que l'authentification Windows. Le passage en mode
mixte se fait dans les propriétés du serveur, à la page Sécurité, et demande un redémarrage du service.
Cette décision appartient au DBA. Si l'instance doit rester en authentification Windows seule, le
serveur MCP peut se connecter avec un compte Windows, avec une limite expliquée dans *Avec un compte
Windows*, plus bas.

#### Étape 2 — Créer le login

Le script `db/Create-DdlReaderLogin.sql` s'exécute sous un compte autorisé à créer des logins et des
utilisateurs. En pratique, un administrateur de l'instance.

Il s'exécute en **mode SQLCMD**. Le script commence par quatre lignes `:setvar`, qui fixent le nom du
login, son mot de passe, le nom du rôle et le nom de la base. Ces lignes ne sont pas du T-SQL : ce sont
des commandes de `sqlcmd`, que SSMS ne comprend qu'en mode SQLCMD (menu *Requête > Mode SQLCMD*). Sans
ce mode, le script s'arrête sur sa première ligne, et rien n'est créé.

Avant de l'exécuter, remplacez les quatre valeurs. Les noms sont des exemples. Le mot de passe doit
être le vôtre.

Le script fait cinq choses, dans cet ordre :

1. il crée le login, au niveau de l'instance ;
2. il crée l'utilisateur correspondant, dans la base ;
3. il crée un rôle, et fait de cet utilisateur un membre de ce rôle ;
4. il accorde au rôle le droit `VIEW DEFINITION`, sur toute la base ou sur un seul schéma ;
5. il refuse au rôle l'exécution de quatre procédures qui écrivent, quand elles existent.

Le voici en entier :

```sql
:setvar Login    ddl_reader_mcp
:setvar Password "Change-this-password-1"
:setvar Role     schema_mcp_reader
:setvar Database demo

-- 1. The login. It is created at the level of the instance, in master, and it is what opens a
--    connection. check_policy applies the password rules of Windows, such as length and complexity.
--    default_database is the database a connection lands in when it names none.
use master;
go

create login [$(Login)]
    with password = N'$(Password)', check_policy = on, default_database = [$(Database)];
go

-- 2. The user. A login opens a connection to the instance; to enter a database, it needs a user in that
--    database, mapped to it.
use [$(Database)];
go

create user [$(Login)] for login [$(Login)];
go

-- 3. A role holds the right, and the user becomes a member of it.
create role [$(Role)];
alter role [$(Role)] add member [$(Login)];
go

-- 4. The one right granted. By default it covers the whole database. To limit it to one schema, comment
--    out the first line below and uncomment the second, with the name of your schema.
grant view definition to [$(Role)];
-- grant view definition on schema::[sales] to [$(Role)];
go

-- 5. The only writes left open by the default rights of a database.
if object_id(N'dbo.sp_creatediagram') is not null deny execute on dbo.sp_creatediagram to [$(Role)];
if object_id(N'dbo.sp_alterdiagram') is not null deny execute on dbo.sp_alterdiagram to [$(Role)];
if object_id(N'dbo.sp_renamediagram') is not null deny execute on dbo.sp_renamediagram to [$(Role)];
if object_id(N'dbo.sp_dropdiagram') is not null deny execute on dbo.sp_dropdiagram to [$(Role)];
go
```

Le fichier du dépôt contient les mêmes instructions, avec des commentaires plus complets, et la façon
de tout défaire.

#### Pourquoi ces choix

**Un seul droit : `VIEW DEFINITION`.** Ce droit permet à un login de lire la définition des objets :
les colonnes d'une table et leurs types, le texte d'une vue ou d'une procédure, les index, les
contraintes. Il ne lui permet pas de lire le contenu d'une table. Il ne lui permet pas non plus
d'exécuter une procédure, ni de modifier quoi que ce soit. C'est le principe du moindre privilège : un
login reçoit ce dont sa tâche a besoin, et rien d'autre. Chaque outil du serveur MCP fonctionne avec ce
seul droit.

**Un rôle, plutôt qu'un droit accordé directement au login.** Le droit est accordé à un rôle, et le
login devient membre de ce rôle. Si un autre compte a un jour besoin du même accès — un second login,
ou un compte Windows —, il suffit de l'ajouter au rôle. Il n'y a aucun nouveau droit à accorder, ni à
revoir. Le fichier du script montre comment faire pour un compte Windows.

**Toute la base, ou un seul schéma.** Par défaut, le droit couvre toute la base. Vous pouvez le limiter
à un schéma : le script contient, en commentaire, la ligne à utiliser à la place. Le serveur MCP n'est
pas informé de cette limite. Il voit simplement moins d'objets. C'est voulu : la limite vit dans les
droits, là où vous la contrôlez, et non dans un réglage du serveur MCP.

**Quatre refus, sur les procédures de diagrammes.** Quand des diagrammes de base de données sont créés
dans SSMS, quatre procédures sont installées dans le schéma `dbo` : `sp_creatediagram`,
`sp_alterdiagram`, `sp_renamediagram` et `sp_dropdiagram`. Elles écrivent dans la table
`dbo.sysdiagrams`. Et le rôle `public`, auquel appartient chaque utilisateur de la base, est autorisé à
les exécuter. C'est la seule voie d'écriture que les droits par défaut d'une base laissent ouverte. Le
script la ferme, procédure par procédure, et seulement là où la procédure existe.

**Aucun autre refus.** Il est tentant de tout refuser d'un coup, par exemple avec un `deny select` sur
toute la base. Ce serait une erreur. Un tel refus s'appliquerait aussi aux vues système du schéma
`sys`, celles qui décrivent les objets de la base. Or c'est exactement ce que lit le serveur MCP : le
login ne verrait plus rien du tout. Le script ne refuse donc que ce qui a été identifié comme une
écriture possible. Les droits que votre base accorde à `public` en plus des droits par défaut restent
accordés, et l'étape 4 est là pour les montrer.

#### Étape 3 — Vérifier ce que le login peut faire

Le script de l'étape 2 est exécuté par un administrateur. Il ne dit donc rien de ce que le login
lui-même peut faire. C'est le rôle du script `db/Verify-DdlReaderRights.sql`.

Ce script s'exécute **connecté avec le login créé à l'étape 2**, dans la base que le serveur MCP lira.
C'est important : le script vérifie les droits de la connexion qui l'exécute. Lancé depuis un compte
administrateur, il répondrait pour l'administrateur. Dans SSMS, ouvrez une nouvelle connexion,
choisissez *Authentification SQL Server*, tapez le nom du login et son mot de passe, puis sélectionnez
la base.

Le script ne modifie rien. Il fait quatre contrôles et rend pour chacun un verdict, PASS ou FAIL :

1. **Le login voit les définitions.** Le script compte les objets visibles et les textes de modules
   lisibles.
2. **Le login ne lit aucune donnée.** Aucune table ni aucune vue ne lui accorde le droit `SELECT`. Et
   une tentative réelle de lecture de la première table de la base est refusée par SQL Server
   (erreur 229).
3. **Le login n'écrit rien.** Il ne détient aucun droit d'insérer, de modifier, de supprimer ou
   d'exécuter, ni aucun droit de créer ou de modifier un objet de la base. Et une tentative réelle de
   création de table est refusée (erreur 262). Cette tentative s'exécute dans une transaction annulée :
   même si elle réussissait, il n'en resterait rien.
4. **Les fonctions de dépendances répondent.** L'outil `find_references` du serveur MCP en a besoin.

Voici le résultat sur la base de démonstration de ce dépôt. Sur votre base, les nombres et le nom de la
table essayée seront les vôtres :

```text
step  check_name              verdict  detail
1     sees the definitions    PASS     35 objects visible, 12 module texts readable
2     reads no data           PASS     0 tables or views readable; reading [legacy].[100%_done] refused (229)
3     writes nothing          PASS     0 rights to write held; creating a table refused (262)
4     reads the dependencies  PASS     0 objects reference [legacy].[100%_done]

conclusion
PASS: this login reads the definitions, and neither reads nor writes the data.
```

Quand un contrôle rend FAIL, la colonne `detail` dit ce qui a été trouvé. Un FAIL en lecture ou en
écriture signifie que le login détient plus de droits que prévu : par un autre rôle, par un droit
accordé à `public`, ou parce que c'est un administrateur. Exécuté sous un compte `sysadmin`, le script
rend bien FAIL sur ces deux contrôles, comme il se doit.

Une fois ce contrôle passé, le login est prêt : vous pouvez déclarer la connexion au serveur MCP (voir
*Les connexions*, plus bas).

#### Étape 4 — Ce que la base accorde à tous ses utilisateurs

Chaque utilisateur d'une base est membre du rôle `public`, et ne peut pas en être retiré. Un droit
accordé à `public` est donc détenu par chaque utilisateur, login du serveur MCP compris, en plus de ce
qui lui est accordé nommément.

Le script `db/Review-PublicPermissions.sql` liste ces droits, hors vues du catalogue. Il s'exécute sous
un compte administrateur de la base. La raison : la vue système qui liste les droits ne montre à un
login que les droits qu'il est autorisé à voir. Exécuté par le login restreint, le script montrerait
moins de droits qu'il n'y en a.

Sur une base neuve, il affiche deux lignes, `VIEW ANY COLUMN ENCRYPTION KEY DEFINITION` et `VIEW ANY
COLUMN MASTER KEY DEFINITION`. Elles donnent accès aux métadonnées des clés *Always Encrypted*, et à
aucune donnée. Si vous voyez d'autres droits `SELECT`, `INSERT`, `UPDATE`, `DELETE` ou `EXECUTE`
accordés à `public`, ils s'appliquent à chaque utilisateur de la base. Ils méritent un examen, quelle
que soit votre décision sur le serveur MCP.

#### Avec un compte Windows

Le serveur MCP peut aussi se connecter en authentification Windows plutôt qu'avec un login SQL Server.
Il se connecte alors avec le compte Windows qui a lancé l'application d'IA : en pratique, votre propre
compte, celui que vous utilisez aussi dans SSMS.

Cela change ce que les droits peuvent garantir. La boucle de travail de ce serveur MCP met en jeu deux
identités : l'agent lit la structure avec les droits du serveur MCP, et vous appliquez les scripts
qu'il propose avec vos propres droits. En authentification Windows, ces deux identités n'en font
qu'une.

- Si votre compte garde les droits avec lesquels vous travaillez, le serveur MCP les détient aussi. Il
  n'exécute toujours que ses propres requêtes, qui lisent le catalogue : ce serveur MCP ne modifiera
  donc pas votre base. Mais la garantie repose alors sur son code, et non plus sur les droits.
- Si votre compte est limité à `VIEW DEFINITION`, la garantie tient de nouveau, mais vous ne pouvez
  plus rien appliquer avec ce compte. Le serveur MCP sert alors à lire et à relire du code, pas à le
  modifier.

Un login SQL Server dédié au serveur MCP, comme celui créé à l'étape 2, sépare les deux identités.
L'authentification Windows convient à un compte qui ne fait que lire, pour une revue ou un audit.

Les droits du login bornent ce serveur MCP, et rien d'autre. Votre application d'IA peut donner à
l'agent d'autres outils : un autre serveur MCP qui exécute du SQL, ou une ligne de commande capable de
lancer `sqlcmd`. Ces outils s'exécutent sous votre compte Windows, quelle que soit la façon dont ce
serveur MCP se connecte, et avec les droits de ce compte. Vérifiez aussi ce qu'ils peuvent faire.

Quel que soit le compte utilisé, ses droits s'appliquent, et c'est à vous de les vérifier. Le plus
simple est d'exécuter le script de l'étape 3 connecté avec ce compte. Pour lui donner exactement les
droits du login de l'étape 2, ajoutez-le au rôle que le script a créé.

Le serveur MCP ne peut pas faire cette vérification à votre place. Un compte peut être restreint sur
certaines tables et pas sur d'autres. Aucune vérification globale ne peut affirmer qu'il ne lit rien.

L'inverse, en revanche, se vérifie. Un compte `sysadmin` peut lire toutes les données. Un compte qui
détient le droit `SELECT` sur toute la base aussi, que ce droit vienne du rôle `db_datareader`, du rôle
`db_owner` ou d'un `grant`. Le serveur MCP détecte ces deux cas et les signale. Il le fait d'abord dans
la console, quand vous testez la connexion avec `configure`. Il le redit une fois, dans le premier
résultat que reçoit l'agent. Et l'outil `server_info` le répète chaque fois qu'on le lui demande. Quand
le serveur MCP ne détecte rien, il ne dit rien. Ce silence n'est pas une garantie.

### Ce que peut révéler le code de votre base

Le droit `VIEW DEFINITION` donne accès au texte des vues, des procédures, des fonctions et des
déclencheurs, tel qu'il a été écrit. Si vous avez écrit en clair dans ce code des mots de passe, des
jetons d'API ou d'autres informations sensibles, l'IA les lira, avec le peu de droits qu'elle a. Et ce
qu'elle lit part chez le fournisseur du modèle d'IA, avec le reste de la structure.

Écrire des secrets dans du code est une mauvaise pratique. Le serveur MCP ne cherche pas à la
compenser. Ce risque n'est d'ailleurs pas propre à l'IA : ces informations sont déjà lisibles par
quiconque a reçu le droit `VIEW DEFINITION`.

Avant d'ouvrir l'accès, vous pouvez rechercher ce genre d'informations dans le code de la base. Par
exemple :

```sql
select object_schema_name(object_id) as schema_name, object_name(object_id) as object_name
from sys.sql_modules
where definition like N'%password%' or definition like N'%token%';
```

## Ce que l'agent obtient

Le serveur MCP sert une boucle de travail simple. L'agent lit la structure de la base par
l'intermédiaire du serveur MCP. Il propose un script. Vous relisez ce script, et vous l'appliquez
vous-même. Le serveur MCP n'écrit jamais dans la base.

Le serveur MCP fournit des faits exacts sur la base. Le modèle d'IA écrit le SQL. Il l'écrit avec les
vrais noms de tables et de colonnes, les vrais types, la vraie nullabilité.

Le serveur MCP propose huit outils, et un neuvième quand vous fournissez un fichier de conventions :

| Outil | Ce qu'il renvoie |
| --- | --- |
| `list_objects` | La liste des tables, vues, procédures, fonctions, déclencheurs et séquences. Elle se filtre par schéma, par nom, par type d'objet ou par date de modification. Pour chaque table, il donne son nombre approximatif de lignes, ce qui fait apparaître d'emblée la table principale parmi ses satellites. Pour chaque vue, procédure, fonction ou déclencheur, il donne la longueur de son texte : l'agent sait ce qu'un objet coûtera à lire avant de le demander. |
| `describe_object` | Un résumé compact de la structure d'un objet. Pour une table : son nombre approximatif de lignes, ses colonnes et leurs types, ses clés, ses index, ses clés étrangères, ses contraintes de vérification et ses déclencheurs. Pour une vue : ses colonnes. Pour une procédure ou une fonction : ses paramètres. Pour un déclencheur : la table sur laquelle il se déclenche, et quand. Sur demande, les descriptions (`MS_Description`) de l'objet et de ses colonnes. |
| `script_object` | Le script `CREATE` complet d'un objet, tel que SSMS le produit. L'agent l'utilise quand il doit modifier l'objet, et `describe_object` quand il lui suffit d'en connaître la structure. |
| `find_references` | Ce qui utilise un objet : les vues, procédures, fonctions et déclencheurs qui s'appuient sur lui, et les tables dont les clés étrangères pointent vers lui. Ou, dans l'autre sens, ce que cet objet utilise. |
| `search_modules` | Les lignes du texte des vues, procédures, fonctions et déclencheurs qui contiennent un fragment, avec leurs numéros de ligne et, sur demande, quelques lignes autour. |
| `find_columns` | Les tables et les vues qui ont une colonne d'un nom donné, avec le type de cette colonne. |
| `use_connection` | Le choix de la base sur laquelle travailler, parmi les connexions que vous avez déclarées. |
| `server_info` | L'emplacement de l'exécutable, sa version, le fichier des connexions, et les commandes qui les gèrent. L'agent peut ainsi vous répondre si vous lui demandez comment supprimer une connexion. |
| `read_conventions` | Seulement quand vous fournissez un fichier de conventions : les conventions d'écriture SQL de votre équipe, telles que vous les avez écrites. Voir *Les conventions de votre équipe*, plus bas. |

### Mesurer l'impact d'une modification

Avant de modifier une table, la question est de savoir ce qui l'utilise. Pour y répondre,
`find_references` et `search_modules` se lisent ensemble.

`find_references` s'appuie sur le graphe de dépendances que SQL Server entretient. Mais ce graphe ne
voit pas le SQL dynamique. Quand une procédure construit une requête dans une chaîne de caractères puis
l'exécute, le nom de la table est dans la chaîne, et SQL Server ne l'enregistre pas comme une
dépendance. `search_modules` cherche dans le texte, et trouve ce nom. Sur une base de production
mesurée, 1,1 % des procédures, vues et fonctions contenaient du SQL dynamique.

### Les conventions de votre équipe (facultatif)

Le serveur MCP peut aussi donner à l'agent les conventions d'écriture SQL de votre équipe : comment
vous nommez les objets, quels types vous employez, comment vous écrivez un script. Il n'impose aucune
convention, et n'en fournit aucune.

Pour vous en servir, écrivez vos conventions dans un fichier Markdown nommé `conventions.md`, et
placez-le à côté de `schema-mcp.exe`. Le serveur MCP propose alors un outil de plus,
`read_conventions`, qui renvoie le fichier tel quel. La description de cet outil demande à l'agent de
l'appeler avant d'écrire du SQL, et de suivre les conventions là où le code existant fait autrement. Si
le fichier se trouve ailleurs, la variable d'environnement `DDL_CONVENTIONS_PATH` donne son
emplacement.

Sans le fichier, l'outil n'existe pas, et il ne coûte rien. Pour cesser de l'utiliser, supprimez ou
renommez le fichier. Le fichier est lu jusqu'à 50 000 caractères ; au-delà, l'agent est prévenu qu'il a
été coupé.

Vous pouvez aussi donner le même fichier à votre agent par ses propres instructions — les instructions
d'un projet dans Claude Desktop, un fichier `CLAUDE.md` ou `AGENTS.md`, un skill — plutôt que par le
serveur MCP. Choisissez l'une des deux voies : un fichier chargé deux fois coûte deux fois, et deux
copies divergent dès que l'une est modifiée.

Le dossier `examples/` contient un exemple d'un tel fichier, avec une note sur la façon de s'en servir.
Ce sont les conventions d'une équipe, publiées à titre d'exemple, et rien de plus.

### Limites connues

- Les dépendances sont données au niveau de l'objet, pas de la colonne. SQL Server ne résout pas les
  dépendances de colonnes de façon fiable, donc le serveur MCP ne les promet pas.
- Un module créé avec l'option `with encryption` n'a pas de texte lisible : `search_modules` ne le
  trouve pas.
- La date de modification d'une table change aussi quand l'un de ses index change. Pour les vues, les
  procédures et les fonctions, elle indique bien la dernière modification du code.
- Les déclencheurs de base de données, ceux qui réagissent aux instructions DDL, ne sont pas couverts :
  ils n'appartiennent à aucun schéma. Les déclencheurs de tables et de vues le sont.

## Ce que cela coûte

### Au modèle d'IA

Chaque réponse d'un outil occupe de la place dans la conversation, et cette place se compte en tokens.
Le serveur MCP limite donc toutes ses réponses. Une liste ne dépasse jamais 200 lignes par défaut. Ce
plafond est fixé par la variable d'environnement `DDL_MAX_RESULTS`, que l'agent ne peut pas modifier.

Quand une demande est trop large, l'outil ne renvoie pas une liste tronquée. Il renvoie la répartition
des résultats : combien d'objets par schéma, par type ou par préfixe de nom. L'agent sait alors comment
resserrer sa demande, ou quelle question vous poser.

Deux outils aident à ne charger que ce qui est utile. `list_objects` donne la longueur du texte d'un
module avant de le charger : certaines procédures dépassent 200 000 caractères. Et `search_modules`
permet à l'agent de lire un passage d'un gros module sans le charger en entier.

Pour la même raison, `describe_object` est l'outil qui fait connaître la structure d'une table : sur
une grosse table d'une base de production, il a renvoyé environ un sixième de ce que renvoyait
`script_object`, en une seconde au lieu de quarante.

Enfin, les définitions des neuf outils représentent environ 3 000 tokens. Un client qui charge ses
outils d'avance les paie dans chaque conversation, même quand elle ne concerne aucune base. Un client
qui charge ses outils à la demande ne les paie que lorsqu'une conversation les cherche.

### À l'instance SQL Server

Le serveur MCP ne lit que le catalogue, jamais une table de données. Les requêtes sur le catalogue sont
en général rapides. La plus coûteuse est une recherche dans le texte de tous les modules, sans filtre.
Sur une base de production de 36 000 objets et 32 000 modules, elle a pris 11 secondes. Et la recherche
s'arrête dès qu'elle a trouvé plus de résultats qu'elle ne peut en renvoyer.

C'est le coût d'un usage de SSMS sans précaution : déplier sans filtre le nœud des procédures stockées
d'une telle base charge aussi des milliers d'objets. Les filtres des outils sont là pour éviter ce
genre de requête.

## Installation

Le serveur MCP est un exécutable unique, `schema-mcp.exe`, d'environ 93 Mo, pour Windows x64. Il
contient le runtime .NET : rien d'autre n'est à installer sur la machine.

### Depuis une release

Chaque release de ce dépôt publie l'exécutable, un fichier contenant son empreinte SHA-256, et l'avis
des composants tiers qu'il contient. Le bloc suivant, exécuté dans PowerShell, télécharge l'exécutable
de la dernière release dans `C:\Tools\SchemaMcp`, puis vérifie son empreinte. Changez le dossier si
vous en préférez un autre.

```powershell
$folder = 'C:\Tools\SchemaMcp'
$release = 'https://github.com/SharpPyxis/SharpPyxis.SqlServer.SchemaMcp/releases/latest/download'
New-Item -ItemType Directory -Force $folder | Out-Null
Invoke-WebRequest "$release/schema-mcp.exe" -OutFile "$folder\schema-mcp.exe"
Invoke-WebRequest "$release/schema-mcp.exe.sha256" -OutFile "$folder\schema-mcp.exe.sha256"
$expected = (Get-Content "$folder\schema-mcp.exe.sha256").Split(' ')[0]
$actual = (Get-FileHash "$folder\schema-mcp.exe" -Algorithm SHA256).Hash
if ($actual -ne $expected) { Remove-Item "$folder\schema-mcp.exe"; throw 'Fingerprint mismatch: the executable was removed.' }
"Fingerprint verified: $actual"
```

L'exécutable et son empreinte viennent de la même release. La vérification prouve donc que le
téléchargement est complet et intact. Elle ne prouve pas qui a construit le fichier.

L'exécutable n'est pas encore signé. Sur une machine où le Contrôle intelligent des applications
(*Smart App Control*) est activé, Windows peut le bloquer.

### Depuis les sources

L'exécutable se construit avec la commande suivante :

```powershell
dotnet publish src/SharpPyxis.SqlServer.SchemaMcp -c Release -r win-x64 --self-contained `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o C:\Tools\SchemaMcp
```

Le résultat est le même exécutable unique que celui publié dans les releases.

### Les connexions

Les connexions se déclarent dans une console, avec l'exécutable lui-même. Vous tapez le nom de
l'instance, la base, le login et le mot de passe. Le serveur MCP ne les demande jamais pendant une
conversation, et l'agent ne les voit jamais.

```text
schema-mcp configure add                 enregistre une nouvelle connexion, après l'avoir testée
schema-mcp configure list                montre ce qui est enregistré
schema-mcp configure test [id]           teste une connexion enregistrée, ou toutes
schema-mcp configure set-password <id>   remplace le mot de passe d'une connexion
schema-mcp configure remove <id>         supprime une connexion
```

Les connexions sont stockées dans un fichier chiffré avec DPAPI, le mécanisme de chiffrement de
Windows, pour le compte Windows courant. Ce chiffrement protège le fichier s'il est volé, ou s'il est
exposé par accident : copié ailleurs, inclus dans une sauvegarde, montré pendant un partage d'écran. Un
programme qui s'exécute sous le même compte Windows peut en revanche le déchiffrer, exactement comme le
fait le serveur MCP. Ce niveau de protection correspond à ce que le mot de passe permet : lire des
définitions d'objets, et rien d'autre.

Le fichier ne peut être déchiffré ni depuis un autre compte Windows, ni sur une autre machine. Si vous
changez de machine, les connexions sont à déclarer de nouveau.

### Le client

Le client est l'application d'IA qui lance le serveur MCP. Chaque client déclare ses serveurs MCP dans
un fichier de configuration qui lui est propre, et dont le format change d'un client à l'autre, parfois
d'une version à la suivante. Ce README ne le décrit donc pour aucun client en particulier. La
déclaration dépend de l'application, quel que soit le modèle d'IA qu'elle fait tourner.

La déclaration tient en trois informations, les mêmes pour tous les clients :

- un nom pour le serveur MCP, par exemple `sqlserver-schema`. Le client s'en sert pour désigner les
  outils du serveur ;
- la commande qui lance le serveur MCP : le chemin complet de l'exécutable, par exemple
  `C:\Tools\SchemaMcp\schema-mcp.exe` ;
- la variable d'environnement `DDL_CONFIG_PATH`, qui indique où se trouve le fichier des connexions.
  Elle est facultative : sans elle, le fichier se trouve dans le dossier `%APPDATA%` de votre compte.

Le plus simple est de demander à l'agent de votre client d'écrire cette déclaration. Donnez-lui ces
trois informations, et demandez-lui où son application déclare les serveurs MCP locaux. L'agent
connaît en général l'application qui le fait tourner, et il peut lire son fichier de configuration. Ce
qu'il en sait peut toutefois dater d'une version précédente, comme la documentation en ligne : le
contrôle qui suit tranche.

La plupart des clients lisent leur configuration au démarrage. Relancez donc le vôtre après la
déclaration, puis demandez à l'agent quelle version du serveur MCP tourne. Pour répondre, l'agent
appelle l'outil `server_info`, qui rend le chemin de l'exécutable, sa version et la connexion
sélectionnée. Si l'agent ne trouve pas cet outil, le client n'a pas chargé le serveur MCP.

Le serveur MCP n'ouvre aucun port réseau. Sa seule connexion est celle qu'il établit vers l'instance
SQL Server.

Si vous avez déclaré plusieurs connexions, aucune n'est choisie au démarrage. L'agent vous demande
alors sur quelle base travailler, et il ne passe à une autre que si vous le lui demandez. Chaque
résultat nomme la base dont il vient : une mauvaise cible se voit tout de suite.

Quatre variables d'environnement configurent le serveur MCP :

| Variable | Rôle |
| --- | --- |
| `DDL_CONFIG_PATH` | L'emplacement du fichier des connexions. |
| `DDL_MAX_RESULTS` | Le nombre maximal de lignes d'une réponse, 200 par défaut. L'agent ne peut pas le modifier. |
| `DDL_CONVENTIONS_PATH` | L'emplacement du fichier de conventions de votre équipe, quand il n'est pas à côté de l'exécutable. Facultatif. |
| `CONNECTION_STRING` | Une connexion unique, déclarée sans le fichier. Elle sert là où DPAPI n'existe pas, hors de Windows. Le mot de passe est alors en clair dans la configuration du client. |

## Essayer sur une base de démonstration

Vous pouvez essayer le serveur MCP sans approcher une base qui compte. Le script
`db/Create-DemoDatabase.sql` crée une vingtaine d'objets, répartis sur quatre schémas, sans aucune
donnée. Il s'exécute dans une base vide, créée pour l'occasion.

Plusieurs de ces objets sont des pièges, et un commentaire annonce chacun d'eux : un nom contenant les
caractères spéciaux de `like`, du SQL dynamique, une référence à une table qui n'existe pas, une vue
cassée par la suppression d'une colonne.

Exécutez-le dans SSMS, ou avec `sqlcmd -I`. Sans l'option `-I`, `sqlcmd` crée les procédures avec
`QUOTED_IDENTIFIER OFF`, et `script_object` le montrera.

## Tests

```powershell
dotnet test ./SharpPyxis.SqlServer.SchemaMcp.slnx -c Release
```

Les tests d'intégration ont besoin de LocalDB. Ils créent la base de démonstration, exécutent les
outils du serveur MCP sur elle, puis la suppriment.

## Licence

Le code de ce dépôt est sous licence MIT, dans `LICENSE`.

L'exécutable publié dans les releases contient aussi le runtime .NET et des bibliothèques tierces,
chacune sous sa propre licence : `THIRD-PARTY-NOTICES.md` les liste. L'une d'elles,
`Microsoft.Data.SqlClient.SNI`, n'est pas open source. Elle est distribuée sous les Microsoft Software
License Terms, qui s'appliquent à l'usage que vous en faites.
