from os.path import exists
from os import system
import sys
import os
import datetime as DT
from pathlib import Path
import json
import re
import difflib
from urllib.parse import urlparse
import pandas as pd
import numpy as np
import matplotlib.pyplot as plt
import matplotlib.dates as mdates
import matplotlib.gridspec as gridspec
import matplotlib.animation as animation
import matplotlib.patches as mpatches
from typing import Dict, Optional

class TimeManagement:
  """
  Refactored for readability:
  - small helpers for tagging and merge rules
  - safe pie chart creation (no negative 'None' slice)
  - central legend creation for multi-plot figures
  """

  DEFAULT_COLORS: Dict[str, str] = {
    "Game": "blue",
    "Main": "green",
    "Side Project": "red",
    "Browser": "orange",
    "Journaling": "yellow",
    "Other": "grey",
    "Social": "purple",
    "None": "black"
  }

  def __init__(self, reloadTime, root_path, save_path, colors: Optional[Dict[str, str]] = None, export = True):
    if root_path is None:
      root_path = os.getcwd()
    self.root_path = root_path
    self.save_path = save_path
    self.reloadTime = reloadTime
    self.colors = colors if colors is not None else dict(self.DEFAULT_COLORS)
    self.export = export

  # Import & Preprocess Data
  # ==================================================================


  # Tagging helpers
  # ----------------
  def _prompt_tag(self, label: str, choices: Dict[str,str]) -> Optional[str]:
    """Prompt user for a tag; return selected tag string or None for skip."""
    prompt = f"{label} \nWhat tag is this (1-{list(choices.keys())[0]} | 2-{list(choices.keys())[1]} | 3-{list(choices.keys())[2]} | 4-{list(choices.keys())[3]} | else-Skip): "
    sel = input(prompt)
    mapping = {"1": list(choices.keys())[0], "2": list(choices.keys())[1], "3": list(choices.keys())[2], "4": list(choices.keys())[3]}
    return mapping.get(sel, None)

  # New helpers for rule-driven tagging
  def load_tag_rules(self, path=None):
    path = path or os.path.join(self.root_path, "data", "tag_rules.json")
    if not os.path.exists(path):
      return []
    try:
      with open(path, "r", encoding="utf-8") as fh:
        return json.load(fh)
    except Exception:
      return []

  def apply_rules(self, process, name, rules):
    s = f"{process} {name}"
    for r in rules:
      try:
        if re.search(r.get("pattern", ""), s, flags=re.IGNORECASE):
          project = r.get("project")
          m = re.search(r.get("pattern", ""), s, flags=re.IGNORECASE)
          if project and m:
            # substitute ${name} style tokens with groupdict values
            def sub(mo):
              key = mo.group(1)
              return m.groupdict().get(key, "") if m and key in m.groupdict() else ""
            project = re.sub(r"\$\{(\w+)\}", sub, project)
          return {"category": r.get("category"), "project": project, "label": r.get("label")}
      except re.error:
        continue
    return None

  def auto_assign_tag(self, process, name, process_tags_df, rules):
    # 1) exact match in process_tags (case-insensitive)
    if not process_tags_df.empty:
      match = process_tags_df[process_tags_df.Process.str.lower() == str(process).lower()]
      if not match.empty:
        row = match.iloc[0]
        return {"category": row.get("Category"), "project": row.get("Project"), "label": row.get("Label")}

    # 2) apply rules from tag_rules.json
    r = self.apply_rules(process, name, rules)
    if r:
      return r

    # 3) special-case Obsidian
    if str(process).lower().startswith("obsidian"):
      filename = str(name).split(" - ")[0].split("-")[0]
      if self.is_date(filename):
        return {"category": "Journaling", "project": "DailyNotes", "label": "Journaling"}
      return {"category": "Side", "project": f"Obsidian:{filename}", "label": filename}

    # 4) basic firefox heuristics
    if str(process).lower().startswith("firefox"):
      nm = str(name).lower()
      if any(x in nm for x in ["youtube", "reddit", "twitch", "netflix", "prime video"]):
        return {"category": "Social", "project": "Browser", "label": "Browser"}
      if any(x in nm for x in ["chatgpt", "python", "tensorflow", "zoom", "tu berlin"]):
        return {"category": "Side", "project": "Research", "label": "Side Project"}
      return {"category": "Browser", "project": "Browser", "label": "Browser"}

    # 5) fuzzy match against existing projects
    existing_projects = []
    if not process_tags_df.empty and "Project" in process_tags_df.columns:
      existing_projects = [p for p in process_tags_df.Project.dropna().unique()]
    best = difflib.get_close_matches(str(name), existing_projects, n=1, cutoff=0.7)
    if best:
      return {"category": None, "project": best[0], "label": best[0]}

    return None

  def tagging(self, app, process_tags):
    """Interactive tagging for unknown processes and Obsidian files.

    New behavior:
    - use rule file (data/tag_rules.json) to auto-assign
    - try fuzzy / heuristics
    - prompt only for unknown items and persist hierarchical columns
    """
    skipped = []
    no_need = ["Firefox Developer Edition", "Firefox", "Visual Studio Code"]

    # load tag rules
    rules = self.load_tag_rules()

    # Ensure process_tags has expected columns
    for c in ["Process", "Category", "Project", "Label"]:
      if c not in process_tags.columns:
        process_tags[c] = None

    # iterate through unique processes
    for process in app.Process.unique():
      if process in no_need:
        continue
      # skip if already present (case-insensitive)
      if process_tags.Process.dropna().str.lower().isin([str(process).lower()]).any():
        continue

      sample_name = ""
      rows = app[app.Process == process]
      if not rows.empty:
        sample_name = str(rows.Name.iloc[0])

      suggestion = self.auto_assign_tag(process, sample_name, process_tags, rules)
      if suggestion:
        display = f"{suggestion.get('category')}/{suggestion.get('project')}/{suggestion.get('label')}"
        conf = input(f"Auto-assign '{process}' -> {display}. Accept? (Enter=Yes / n=No / e=Edit): ")
        if conf.strip().lower() in ["", "y", "yes"]:
          process_tags.loc[len(process_tags)] = [process, suggestion.get('category'), suggestion.get('project'), suggestion.get('label')]
          continue
        if conf.strip().lower() == 'n':
          skipped.append(process)
          continue
        # if edit, fallthrough to manual prompt

      # Manual prompt if no suggestion or edit requested
      cat = input(f"Category for '{process}' (Game/Main/Side/Other) or Enter to skip: ")
      if not cat:
        skipped.append(process)
        continue
      proj = input(f"Project name for '{process}' (or Enter to use process name): ")
      if not proj:
        proj = process
      label = input(f"Label (display) for '{process}' (Enter to use project '{proj}'): ") or proj
      process_tags.loc[len(process_tags)] = [process, cat, proj, label]

    # Special case: Obsidian — split by file name prefix and prompt/save per-file rules
    if (app.Process == "Obsidian").any():
      obsidian_names = app[app.Process == "Obsidian"].Name.apply(lambda x: str(x).split(' - ')[0].split('-')[0]).unique()
      for filename in obsidian_names:
        if self.is_date(filename):
          # journaling handled in merge_tags
          continue
        name = f"Obsidian-{filename}"
        if process_tags.Process.dropna().str.lower().isin([str(name).lower()]).any():
          continue
        suggestion = self.auto_assign_tag(name, filename, process_tags, rules)
        if suggestion:
          conf = input(f"Auto-assign '{name}' -> {suggestion}. Accept? (Enter=Yes / n=No / e=Edit): ")
          if conf.strip().lower() in ["", "y", "yes"]:
            process_tags.loc[len(process_tags)] = [name, suggestion.get('category'), suggestion.get('project'), suggestion.get('label')]
            continue
        # Manual fallback
        cat = input(f"Category for '{name}' (Main/Side/Game/Other) or Enter to skip: ")
        if not cat:
          skipped.append(name)
          continue
        proj = input(f"Project name for '{name}' (or Enter to use '{filename}'): ") or filename
        label = input(f"Label for '{name}' (Enter to use project '{proj}'): ") or proj
        process_tags.loc[len(process_tags)] = [name, cat, proj, label]

    # Replace "Obsidian" process entries in app with "Obsidian-<filename>"
    for i, row in app[app.Process == "Obsidian"].iterrows():
      filename = str(row.Name).split(" - ")[0]
      app.at[i, "Process"] = f"Obsidian-{filename}"

    if skipped:
      print("Skipped tagging on: ", skipped)

    # normalize and save
    process_tags["Process"] = process_tags["Process"].astype(str)
    # ensure columns order
    cols = [c for c in ["Process", "Category", "Project", "Label"] if c in process_tags.columns]
    process_tags = process_tags[cols]
    process_tags.sort_values(by=[c for c in ["Category", "Project", "Process"] if c in process_tags.columns], inplace=True, na_position='last')
    process_tags.reset_index(drop=True, inplace=True)
    process_tags.to_csv(os.path.join(self.root_path, "data", "process_tags.csv"), index=False)
    return process_tags

  # Merge tags and apply special rules
  # -----------------------------------
  def merge_tags(self, app, process_tags):
    app = pd.merge(app, process_tags, how="left", left_on="Process", right_on="Process")
    # populate legacy 'Tag' column used by plotting and other methods
    if "Label" in app.columns:
      app["Tag"] = app["Label"].fillna(app.get("Tag"))
    elif "Project" in app.columns:
      app["Tag"] = app["Project"].fillna(app.get("Tag"))

    # apply special rules after initial assignment
    self._apply_vsc_rules(app)
    self._apply_firefox_rules(app)
    self._apply_obsidian_rules(app)
    # ensure Tag exists
    if "Tag" not in app.columns:
      app["Tag"] = None
    return app

  def _apply_vsc_rules(self, app):
    for i, row in app[app.Process == "Visual Studio Code"].iterrows():
      if any(x in row.Name for x in ["Passenger_Seo", "matar (Workspace)"]):
        app.at[i, "Tag"] = "Main"
      else:
        app.at[i, "Tag"] = "Side Project"

  def _apply_firefox_rules(self, app):
    for i, row in app[app.Process == "Firefox Developer Edition"].iterrows():
      name = row.Name
      if any(x in name for x in ["YouTube", "Reddit", "Twitch", "Netflix", "Prime Video"]):
        app.at[i, "Tag"] = "Social"
      elif any(x in name for x in ["ChatGPT", "python", "tensorflow", "TensorFlow", "keras", "Zoom", "TU Berlin"]):
        app.at[i, "Tag"] = "Side Project"
      elif any(x in name for x in ["Unity", "unity", "c#", "C#"]):
        app.at[i, "Tag"] = "Main"
      else:
        app.at[i, "Tag"] = "Browser"

  def _apply_obsidian_rules(self, app):
    # Rows where Process starts with "Obsidian-": mark journaling if the filename is a date
    mask = app.Process.str.startswith("Obsidian-")
    for i, row in app[mask].iterrows():
      filename = row.Name.split(" - ")[0]
      if self.is_date(filename):
        app.at[i, "Tag"] = "Journaling"

  # Effective day calculation
  # -------------------------
  def create_effective_day(self, app):
    threshold_hour = 7  # entries before 07:00 belong to previous effective day
    app['Effective_Day'] = app['Start'].apply(lambda x: x - DT.timedelta(days=1) if x.hour < threshold_hour else x)
    app['Effective_Day'] = app['Effective_Day'].dt.floor('D')
    return app

  # Plot helpers
  # ==================================================================
  def _legend_handles(self):
    return [mpatches.Patch(color=color, label=label) for label, color in self.colors.items()]

  # Multi-figure assembly
  # ---------------------
  def three_week_summary(self, td):
    rows, cols = 4, 7
    fig = plt.figure(figsize=(100, 100))
    plt.rcParams.update({'font.size': 22})
    gs = gridspec.GridSpec(rows, cols, figure=fig, hspace=0.1, top=0.9, bottom=0.1)
    used_axes = set()
    fd = td - DT.timedelta(days=td.weekday()+14)

    # Create pies
    for i in range(21):
      date = fd + DT.timedelta(days=i)
      ax = self.pie_chart(self.app, date, ax=plt.subplot(gs[int(i/cols), i%cols]))
      weekday = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"][date.weekday()]
      if ax is not None:
        ax.set_title(weekday + " " + str(date))
        used_axes.add((int(i/cols), i%cols))

    # One unified legend on the figure (outside subplots)
    legend_handles = self._legend_handles()
    fig.legend(handles=legend_handles, loc='center left', bbox_to_anchor=(0.92, 0.5))

    # Line charts (bottom row)
    for i in range(3):
      date = td - DT.timedelta(days=2-i)
      ax = self.line_chart(self.app, date, ax=plt.subplot(gs[3, 2*i:2*(i+1)]))
      weekday = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"][date.weekday()]
      if ax is not None:
        ax.legend().set_visible(i == 0)
        ax.set_title(weekday + " " + str(date))
        used_axes.update({(3, j) for j in range(2*i, 2*(i+1))})

    # Turn off unused axes
    for row in range(rows):
      for col in range(cols):
        if (row, col) not in used_axes:
          ax = plt.subplot(gs[row, col])
          ax.axis('off')

    fig.canvas.manager.set_window_title('Time Management')
    fig.set_size_inches(50, 25, forward=True)
    plt.savefig(os.path.join(self.save_path, "three_week_summary.jpg"))
    return

  def month_view(self, month):
    rows, cols = 5, 7
    fig = plt.figure(figsize=(100, 100))
    plt.rcParams.update({'font.size': 22})
    gs = gridspec.GridSpec(rows, cols, figure=fig, hspace=0.1, top=0.9, bottom=0.1)
    used_axes = set()
    fd = DT.date.today().replace(month=month, day=1)
    td = (DT.date.today().replace(month=month+1, day=1) - DT.timedelta(days=1))
    days = td.day

    for i, j in enumerate(range(fd.weekday(), days+1)):
      date = fd + DT.timedelta(days=i)
      ax = self.pie_chart(self.app, date, ax=plt.subplot(gs[int(j/cols), j%cols]))
      if ax is not None:
        ax.set_title(["Mon","Tue","Wed","Thu","Fri","Sat","Sun"][date.weekday()] + " " + str(date))
        used_axes.add((int(i/cols), i%cols))

    for row in range(rows):
      for col in range(cols):
        if (row, col) not in used_axes:
          ax = plt.subplot(gs[row, col])
          ax.axis('off')

    fig.legend(handles=self._legend_handles(), loc='center left', bbox_to_anchor=(0.92, 0.5))
    fig.canvas.manager.set_window_title('Time Management')
    fig.set_size_inches(50, 25, forward=True)
    plt.savefig(os.path.join(self.save_path, f"month_view_{month}.jpg"))
    return

  def summary(self, td):
    fig = plt.figure(figsize=(100, 100))
    gs = gridspec.GridSpec(3, 3, figure=fig, hspace=0.1, top=0.9, bottom=0.1)

    ax = self.line_chart(self.app, td, ax=plt.subplot(gs[0,:]))
    ax.set_title("Today's recap")

    self.pie_chart(self.app, td - DT.timedelta(days=2), plt.subplot(gs[1,0]))
    ax = self.pie_chart(self.app, td - DT.timedelta(days=1), plt.subplot(gs[1,1]))
    self.pie_chart(self.app, td, plt.subplot(gs[1,2]))
    ax.set_title("3day recap")

    week_ago = td - DT.timedelta(days=7)
    ax = self.bar_chart(self.app, week_ago, td, ax=plt.subplot(gs[2,:]))
    ax.set_title("Week recap")

    fig.canvas.manager.set_window_title('Time Management')
    fig.set_size_inches(10, 9, forward=True)
    plt.savefig(os.path.join(self.save_path, "summary.jpg"))
    return

  def week_summary(self, td = DT.date.today()):
    fig = plt.figure(figsize=(100, 100))
    gs = gridspec.GridSpec(7, 2, figure=fig)

    for i in range(7):
      date = td - DT.timedelta(days=7-i)
      ax = self.line_chart(self.app, date, ax=plt.subplot(gs[i, 0]))
      ax.legend().set_visible(i == 0)
      self.pie_chart(self.app, date, plt.subplot(gs[i, 1]))

    fig.canvas.manager.set_window_title('Time Management')
    fig.set_size_inches(10, 13, forward=True)
    plt.tight_layout()
    plt.savefig(os.path.join(self.save_path, "week_report.jpg"))

  # Graph Creation
  # ==================================================================
  def line_chart(self, app, day, ax=None):
    day = pd.to_datetime(day)
    grouped_df = app[app["Effective_Day"]== day]

    unique_tags = grouped_df['Tag'].unique()
    new_entries = pd.DataFrame({
        'Tag': unique_tags,
        'Duration': pd.to_timedelta(np.zeros(len(unique_tags)), unit='s'),
        'End': [grouped_df[grouped_df['Tag'] == tag]['Start'].min() for tag in unique_tags],
        'Start': [grouped_df[grouped_df['Tag'] == tag]['Start'].min() for tag in unique_tags]
    })
    grouped_df = pd.concat([grouped_df, new_entries], ignore_index=True)
    grouped_df = grouped_df.groupby(["Tag", "End"])["Duration"].sum().reset_index()
    grouped_df["Duration"] = grouped_df["Duration"].dt.total_seconds()/3600
    grouped_df.sort_values(by="End", inplace=True)
    grouped_df["Cumulative Duration"] = grouped_df.groupby("Tag")["Duration"].cumsum()
    pivot_df = grouped_df.pivot(index="End", columns="Tag", values="Cumulative Duration")
    pivot_df = pivot_df.ffill()

    if not pivot_df.empty:
      ax = pivot_df.plot(kind="line", grid=True, color=self.colors, figsize=(10, 5), ax=ax)
      ax.xaxis.set_major_formatter(mdates.DateFormatter('%H'))
      ax.set_xlabel("")
      ax.set_ylim(bottom=0)
      ax.legend(loc='upper left')
      return ax
    return None

  def pie_chart(self, app, day, full_day = 15, ax=None):
    day = pd.to_datetime(day)
    grouped_df = app.groupby(["Effective_Day", "Tag"])["Duration"].sum().reset_index()
    grouped_df = grouped_df[grouped_df["Effective_Day"] == day]
    grouped_df["Duration"] = grouped_df["Duration"].dt.total_seconds()/3600
    grouped_df["Effective_Day"] = pd.to_datetime(grouped_df["Effective_Day"])
    grouped_df = grouped_df.set_index("Tag", drop=True)
    grouped_df.sort_values(by="Tag", inplace=True)

    total_hours = grouped_df["Duration"].sum()
    none_val = max(0.0, full_day - total_hours)
    if total_hours > 0 and none_val > 0:
      grouped_df.loc["None"] = {"Effective_Day": day, "Duration": none_val}

    # map colors robustly (fallback to black)
    colors = [self.colors.get(tag, "black") for tag in grouped_df.index]

    def label_func(pct, allvals):
      absolute = pct/100.*np.sum(allvals)
      return "{:.1f} h".format(absolute)

    if not grouped_df.empty:
      ax = grouped_df.plot(
        kind="pie",
        y="Duration",
        legend=False,
        labels=None,
        title=day.strftime("%a %d.%m"),
        ylabel="",
        autopct=lambda pct: label_func(pct, grouped_df["Duration"]),
        ax=ax,
        colors=colors,
        startangle=90)
      return ax
    return None

  def bar_chart(self, app, start_date=None, end_date=None, ax=None):
    start_date = pd.to_datetime(start_date)
    end_date = pd.to_datetime(end_date)

    grouped_df = app.groupby(["Effective_Day", "Tag"])["Duration"].sum().reset_index()
    grouped_df["Effective_Day"] = pd.to_datetime(grouped_df["Effective_Day"])
    if start_date is not None:
      grouped_df = grouped_df[grouped_df["Effective_Day"] > start_date]
    if end_date is not None:
      grouped_df = grouped_df[grouped_df["Effective_Day"] <= end_date]
    grouped_df["Duration"] = grouped_df["Duration"].dt.total_seconds()/3600
    pivot_df = grouped_df.pivot(index="Effective_Day", columns="Tag", values="Duration")
    pivot_df.sort_index()
    pivot_df.index = pivot_df.index.strftime("%a %d-%m")
    ax = pivot_df.plot(kind="bar", stacked=False, figsize=(10, 5), grid=True, color=self.colors, ax=ax)
    ax.set_ylabel("")
    ax.set_xlabel("")
    ax.get_legend().remove()
    plt.xticks(rotation=45)
    return ax

  # Other
  # ==================================================================
  def update_data(self):
    self.export_app_data()
    self.load_files()
    self.tagging()
    self.merge_tags()

  def continuous_day_chart(self):
    fig = plt.figure(figsize=(10, 5))
    fig.canvas.manager.set_window_title('Day Time Management')
    gs = gridspec.GridSpec(1, 1, figure=fig)
    self.day_today = DT.date.today()
    self.day_ax = plt.subplot(gs[0,0])
    ani = animation.FuncAnimation(fig, self.update_day_chart, interval=self.reloadTime*1000)
    plt.show()
    return

  def update_day_chart(self, *args):
    self.update_data()
    self.day_ax.clear()
    self.line_chart(self.day_today, ax=self.day_ax)
    return

if __name__ == "__main__":
  # Configs:
  td = DT.date.today()
  fd = td - DT.timedelta(days=24)
  reloadTime = 600
  root_path = "/Users/matar/Documents/PugiosDocuments/OwnProjects/TimeManagement"
  save_path = root_path
  colors = {"Game": "blue", "Main":"green", "Side Project": "red", "Browser": "orange", "Journaling": "yellow", "Other":"grey", "Social":"purple", "None": "black"}
  export = True
  month = td.month

  for arg in sys.argv:
    if arg.startswith("-sp"):
      save_path = arg.split("=")[1]
    elif arg.startswith("-debug"):
      export = False

  tm = TimeManagement(reloadTime, root_path, save_path, colors, export)

  print(f"Data from: {fd} - {td}")
  tm.import_and_preprocess(fd, td)
  tm.three_week_summary(td)
  tm.month_import_and_preprocess(month)
  tm.month_view(month)