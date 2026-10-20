# Blackjack Pilot

Plays blackjack hands through your connected SimpleMiniGames seat using the
table's available actions. Requires SimpleMiniGames 1.0.2.5.

1. Join your dealer's blackjack seat in SimpleMiniGames using your player link.
2. Open `/blackjackpilot` and match the rules to your dealer's table.
3. Check **These rules match my table**, then click **Turn on**.
4. Click **Turn off** or use `/blackjackpilot off` to stop.

The plugin starts off after loading and stops on logout, disconnect, a changed
seat, or an unresolved action. Reconnect and check the table before enabling
again. Turning it off cannot retract an action already sent.

It chooses between hit, stand, double, split, and surrender by expected gil
return. You and your dealer control joining, bets, bank transfers, and cashout.
Splitting and doubling can increase the wager on a hand.

Use tables where the dealer stands on hard 17, with the displayed soft-17,
double, and split settings. The model treats ranks as independent random draws.
For a finite shoe it is an approximation, without card counting. Surrender is
modeled as a half-stake loss; without a dealer blackjack check, a dealer natural
loses the full stake, including split and doubled stakes. Charlie is modeled as
an even-money automatic win except against dealer blackjack. Do not enable it
on tables with different payouts, bonus rules, or rule precedence.

Expected return is not a guaranteed win, and maximizing return does not mean
winning the largest possible percentage of hands. The plugin uses only your
hand and the dealer's visible card. It does not place new bets or increase
bets to recover losses.

`/blackjackpilot status` shows its current state. If an action is unresolved,
wait for Gamba's hand display to update before enabling again.
