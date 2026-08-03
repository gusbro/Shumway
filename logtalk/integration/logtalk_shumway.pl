%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
%
%  Logtalk launcher for the Shumway REPL.
%  SPDX-License-Identifier: MIT
%
%  Consults the Shumway backend adapter followed by the Logtalk core, taking
%  the installation root from the LOGTALKHOME environment variable (resolved
%  with getenv/2 — Shumway's consult/1 does not expand $VARs itself).
%
%  Run as:
%
%      set LOGTALKHOME=<Logtalk installation>
%      set LOGTALKUSER=<Logtalk installation>
%      shumway integration/logtalk_shumway.pl
%
%  Equivalent to consulting the three files by hand, in this order:
%
%      shumway adapters/shumway.pl paths/paths.pl core/core.pl
%
%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%

:- initialization((
	getenv('LOGTALKHOME', Home),
	atom_concat(Home, '/adapters/shumway.pl', Adapter),
	consult(Adapter),
	atom_concat(Home, '/paths/paths.pl', Paths),
	consult(Paths),
	atom_concat(Home, '/core/core.pl', Core),
	consult(Core)
)).
