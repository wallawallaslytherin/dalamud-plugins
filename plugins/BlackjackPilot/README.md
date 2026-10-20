# Blackjack Pilot

Plays your blackjack hand in Aetherphone’s Gamba. Requires Aetherphone 1.3.3.0.

1. Open Gamba and sit at a blackjack table.
2. Open `/blackjackpilot` and click **Turn on**, or use `/blackjackpilot on`.
3. Click **Turn off** or use `/blackjackpilot off` to stop.

Use **Finish this hand** or `/blackjackpilot finish` to finish your current wager
and stop before placing another bet. The window shows completed hands and your
session’s net result. Betting preferences are saved; autoplay always starts off.

Table rules are read automatically. Autoplay starts off and stops if your seat
changes, Gamba disconnects, the rules change, or an action remains unresolved.
Check the table before enabling again. Turning off cannot retract a sent action.

It chooses hit, stand, double, split, or surrender for expected return and declines
insurance. Splitting and doubling can increase the stake on your current hand.
Automatic betting supports gil and practice tables. The default is the table
minimum with a session loss budget of 10% of your starting table stack.
Enable **Bet 0.5% of stack** for an amount that follows your current stack.
This manages exposure; it does not improve the odds. Both modes reserve enough
for the table's maximum splits and doubles, and stop before another hand would
exceed the budget. A new session starts when you turn autoplay on again.
Change settings while autoplay is off. Uncheck **Place bets** to bet manually.
You control seating, transfers, rebuys, and cashout. Avoid manual bets or stack
transfers during an automatic session; they change its budget accounting.

The strategy accounts for cards visible in the current hand and the table’s
dealer-peek rule. Split decisions and calculations that exceed the time limit use
an approximation, shown in the window. Model return is a table estimate, not a
prediction of your session’s results. No strategy guarantees wins.
Use `/blackjackpilot status` to check its state.
