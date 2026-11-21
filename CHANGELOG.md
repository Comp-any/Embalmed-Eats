# Changelog

Each version's section becomes its GitHub release notes, and from there its changelog on the Mod DB.

## 1.2.1

- Fixed: with Vintage Doctor, single player (and multiplayer) always showed the problem "Taking Embalmed Eats items from the creative tabs may give you the wrong item". The tabs were fine: the check read the items of your game's lists before the game had looked them up, so they never matched the server's. It now compares each tab the way the creative inventory shows it.
- If the tabs really do differ, the card now says how: which foods are in the tabs on only one side, which tabs have a different number of entries, and whether your game had to make the tabs itself.

## 1.2.0

- **Works with Vintage Doctor.** With Vintage Doctor installed, its check-up says whether Embalmed Eats set itself up properly: its changes to the game, the food it found, whether each of the four methods works (the wax and resin items are in the game, formaldehyde solution and curing salt can be made, the amounts fit in a barrel or a stack), and settings that don't do what was most likely meant (poisoning that never wears off, stages that can't be reached, mistyped entries in the food lists, and more). Many cards have a button that fixes it.
- **Your game against the server:** the doctor also says when your game and the server don't agree on Embalmed Eats: different creative tabs (which would give you the wrong item), settings that didn't arrive, or a different idea of what's food.
- **Can this be preserved?** The doctor's page about any item or block says whether Embalmed Eats can preserve it and why (it feeds you, it rots, a setting rules it out...), and how. Holding a preserved item, it says which kind it is and where it can go.
- **Its own page**, "Something is wrong with preserved food" (`/doctor embalmedeats`): what counts as food and what each list entry matches, the methods, where preserved food can go, the poisoning stages and who is poisoned right now (with a button to cure them), and each of its changes to the game. `/doctor embalmedeats item <code>` goes through the rules for one thing.
- The doctor also explains Embalmed Eats' lines in the log.
- **Without Vintage Doctor:** `/embalmedeats check` shows the same checks as text, and `/embalmedeats fix` does what their buttons do. Nothing else changes; Vintage Doctor is not needed.
- A settings file that can't be read (a missing comma, say) is now kept in `ModData/embalmedeats/backups` before the defaults are written over it, so the changes in it aren't lost. Fixes that change the settings keep the old file there too.
- The handbook guide now says the formaldehyde solution needs Aqua Vitae (spirits distilled a second time), not just "alcohol", and that salt and lime make 2 curing salt.

## 1.1.2

- The mod's `modinfo.json` now gives its GitHub page as its website, so tools that read it (such as Vintage Auto Update) can find its releases there too. Nothing else changes; players and servers on 1.1.1 can mix with 1.1.2.

## 1.1.1

- Fixed: taking items from the Embalmed Eats creative tabs could give you the wrong item (this could also affect other creative tabs). The server and players now have exactly the same creative lists.
- The server and every player need this version.

## 1.1.0

- Preserved food can go on the ground with shift + right-click, even food that normally can't (cheese, butter, pemmican and others). Fresh food that can't normally go on the ground still can't.
- Clicking a preserved item in the handbook, or pressing the handbook key while hovering one, opens the Embalmed Eats guide, which explains how each kind is made.
- New `/embalmedeats help` command (or just `/embalmedeats`) lists all the commands.
- Four new creative tabs: Embalmed Eats: Wax-sealed, Lacquered, Embalmed and Mummified, with every food preserved each way.

## 1.0.2

- New icon: a pretty cake sitting in a pool of formaldehyde.

## 1.0.1

- New icon.

## 1.0.0

- First release.
- Preserve any food, vanilla or modded, so it never rots and keeps its look: wax dipping, resin lacquer, a formaldehyde barrel soak, or a salt and lime barrel cure.
- Preserved food goes on shelves and in display cases.
- Eating preserved food makes you vomit, and it gets worse the more you eat, up to a lethal dose.
- Commands, a config file and a handbook guide.
