from os.path import exists
from os import system
import sys
import os
import datetime as DT
import pandas as pd
import numpy as np
import matplotlib.pyplot as plt
import matplotlib.dates as mdates
import matplotlib.gridspec as gridspec
import matplotlib.animation as animation
import matplotlib.patches as mpatches

class TimeManagement:
  def __init__(self, reloadTime, root_path, save_path, colors, export = True):
    if(root_path is None):
      root_path = os.getcwd()
    if(colors is None):
      colors = {"Game": "blue", "Main":"green", "Side Project": "red", "Browser": "orange", "Other":"yellow", "Social":"purple"}
    
    self.root_path = root_path
    self.save_path = save_path
    self.reloadTime = reloadTime
    self.colors = colors
    self.export = export

    return
  
  # Import & Preprocess Data
  # ==================================================================
  def import_and_preprocess(self, fd=None, td=None):
    if self.export:
      self.export_app_data(fd, td) # debug!
    app, process_tags = self.load_files()
    process_tags = self.tagging(app, process_tags)
    app = self.merge_tags(app, process_tags)
    app = self.create_effective_day(app)
    
    self.app = app
  
  def month_import_and_preprocess(self, month):
    today = DT.date.today()
    fd = today.replace(month=month, day=1)
    # get last day of month
    if month == 12:
      td = today.replace(day=31)
    else:
      td = today.replace(month=month+1, day=1) - DT.timedelta(days=1)

    self.import_and_preprocess(fd, td)

  def export_app_data(self, fd, td):
    app_path = self.root_path + "/data/applications.csv"
    if fd is None and td is None:
      system('"/Program Files/ManicTime/mtc" export ManicTime/Applications ' + app_path)
    else:
      system('"/Program Files/ManicTime/mtc" export ManicTime/Applications ' + app_path + " /fd:" + str(fd) + " /td:" + str(td))
    return

  def load_files(self):
    # Application file from ManicTime
    app = pd.read_csv(self.root_path + "/data/applications.csv", delimiter=",")
    app.Start = pd.to_datetime(app.Start)
    app.End = pd.to_datetime(app.End)
    app.Duration = pd.to_timedelta(app.Duration)
    # app["Day"] = app.Start.dt.floor("d")

    # Read in or Create Processed Tags Table
    process_tags_path = self.root_path + "/data/process_tags.csv"
    if (exists(process_tags_path)):
      process_tags = pd.read_csv(process_tags_path, index_col=0)
    else:
      process_tags = pd.DataFrame(columns=["Process", "Tag"])
    return app, process_tags
  
  def is_date(self, date_string):
    try:
      # Check for DD.MM.YYYY format
      DT.datetime.strptime(date_string, "%d.%m.%Y")
      return True
    except ValueError:
      pass

    try:
      # Check for YYYY-MM-DD format
      DT.datetime.strptime(date_string, "%Y-%m-%d")
      return True
    except ValueError:
      pass

    return False

  def tagging(self, app, process_tags):
    # Fill the missing Tags!
    skipped = []
    
    # Go through application table, look for things that are no in the Process Tags list AND are not a Firefox Window or VSC Window 
    no_need = ["Firefox Developer Edition", "Firefox", "Visual Studio Code", "Obsidian"]
    for process in app.Process.unique():
      if((not process_tags.Process.isin([process]).any()) and (process not in no_need)):
        tag = input(f"{process} \nWhat tag is this process (1-Main | 2-Side Project | 3-Game | 4-Other | else-Skip): ")

        if(tag == "1"):
          process_tags.loc[process_tags.shape[0]] = [process, "Main"]
        elif(tag == "2"):
          process_tags.loc[process_tags.shape[0]] = [process, "Side Project"]
        elif(tag == "3"):
          process_tags.loc[process_tags.shape[0]] = [process, "Game"]
        elif(tag == "4"):
          process_tags.loc[process_tags.shape[0]] = [process, "Other"]
        else:
          skipped.append(process)
    
    # Special case for Obsidian files
    for filename in app[app.Process == "Obsidian"].Name.apply(lambda x: x.split(' - ')[0]).unique():
      # filename = filename.split(" - ")[0]
      name = "Obsidian-" + filename
      # If Daily Note
      if self.is_date(filename):
        # Will be sorted into Journaling at merge_tags()
        continue
      elif (not process_tags.Process.isin([name]).any()):
        tag = input(f"{name} \nWhat tag is this file (1-Main | 2-Side Project | 3-Journaling | 4-Other | else-Skip): ")

        if(tag == "1"):
          process_tags.loc[process_tags.shape[0]] = [name, "Main"]
        elif(tag == "2"):
          process_tags.loc[process_tags.shape[0]] = [name, "Side Project"]
        elif(tag == "3"):
          process_tags.loc[process_tags.shape[0]] = [name, "Journaling"]
        elif(tag == "4"):
          process_tags.loc[process_tags.shape[0]] = [name, "Other"]
        else:
          skipped.append(name)
    
    # replace "Obsidian" in Process Column with Obsidian+filename
    for i, row in app[app.Process == "Obsidian"].iterrows():
      app.at[i, "Process"] = "Obsidian-" + row.Name.split(" - ")[0]

    if(len(skipped) > 0):
      print("Skipped tagging on: ", skipped)
    
    process_tags.sort_values(by=["Tag", "Process"], inplace=True)
    process_tags.reset_index(drop=True, inplace=True)

    # Save any changes done to the Processed Tags Table
    process_tags.to_csv(self.root_path + "/data/process_tags.csv")
    return process_tags
  
  def merge_tags(self, app, process_tags):
    app = pd.merge(app, process_tags, how="left", left_on="Process", right_on="Process")

    # Special Case VSC
    for i, row in app[app.Process == "Visual Studio Code"].iterrows():
      if(any(x in row.Name for x in ["Passenger_Seo", "matar (Workspace)"])):
        app.at[i, "Tag"] = "Main"
      else:
        app.at[i, "Tag"] = "Side Project"
    
    # Special Case Firefox
    for i, row in app[app.Process == "Firefox Developer Edition"].iterrows():
      if(any(x in row.Name for x in ["YouTube", "Reddit", "Twitch", "Netflix", "Prime Video"])):
        app.at[i, "Tag"] = "Social"
      elif(any(x in row.Name for x in ["ChatGPT", "python", "tensorflow", "TensorFlow", "keras", "Zoom", "TU Berlin"])):
        app.at[i, "Tag"] = "Side Project"
      elif(any(x in row.Name for x in ["Unity", "unity", "c#", "C#"])):
        app.at[i, "Tag"] = "Main"
      else:
        #! Sadly there is no other way to determine what task I was actually doing so everything else goes to Browser
        app.at[i, "Tag"] = "Browser"
    
    # Special Case Obisidan Daily Notes
    for i, row in app[app.Process.str.startswith("Obsidian-")].iterrows():
      if(self.is_date(row.Name.split(" - ")[0])):
        app.at[i, "Tag"] = "Journaling"

    
    #? These are all the websites that needed to be stored as "Browser" since I dont know where to assign them to
    # other = app.loc[(app.Process == "Firefox Developer Edition") & (app.Tag == "Browser"), "Name"].unique()
    # pprint(other)
    return app
  
  def create_effective_day(self, app):
    threshold_hour = 7 #7AM

    # Create a new column 'Effective_Day' by shifting dates back for any time before threshold
    app['Effective_Day'] = app['Start'].apply(lambda x: x - DT.timedelta(days=1) if x.hour < threshold_hour else x)
    app['Effective_Day'] = app['Effective_Day'].dt.floor('D')  # Only keep the date part

    return app
  
  # Graph Assembly
  # ==================================================================
  def three_week_summary(self, td):
    rows = 4
    cols = 7
    
    fig = plt.figure(figsize=(100, 100))
    plt.rcParams.update({'font.size': 22})
    gs = gridspec.GridSpec(rows, cols, figure=fig, hspace=0.1, top=0.9, bottom=0.1)
    # gs = gridspec.GridSpec(rows, cols, figure=fig)

    used_axes = set()

    # Pie Charts
    # ==========
    # monday 2 weeks ago
    fd = td - DT.timedelta(days=td.weekday()+14)

    for i in range(21):
      date = fd + DT.timedelta(days=i)

      # ax = self.line_chart(self.app, date, ax=plt.subplot(gs[int(i/cols), i%cols]))
      ax = self.pie_chart(self.app, date, ax=plt.subplot(gs[int(i/cols), i%cols]))
      
      weekday = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"]
      weekday = weekday[date.weekday()]

      if weekday == "Mon" and i/7 == 0:
        # ax.legend = True
        # ax.legend = list(self.colors.keys()), loc='center left', bbox_to_anchor=(-0.5, 0.5)
        # ax.legend(list(self.colors.keys()), loc="center left")
        
        legend_handles = [mpatches.Patch(color=color, label=label) for label, color in self.colors.items()]
        fig.legend(handles=legend_handles, loc='center left', bbox_to_anchor=(0.92, 0.5))

      
      if ax is not None:
        ax.set_title(weekday + " " + str(date))
        used_axes.add((int(i / cols), i % cols))

    # Line Charts
    # ==========
    for i in range(3):  # One line chart per day
      date = td - DT.timedelta(days=2-i)
      ax = self.line_chart(self.app, date, ax=plt.subplot(gs[3, 2 * i:2 * (i + 1)]))  # Allocate equal space
      
      weekday = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"]
      weekday = weekday[date.weekday()]

      if ax is not None:
        ax.legend().set_visible(i == 0) 
        ax.set_title(weekday + " " + str(date))
        used_axes.update({(3, j) for j in range(2 * i, 2 * (i + 1))})
    
    for row in range(rows):
      for col in range(cols):
        if (row, col) not in used_axes:
          ax = plt.subplot(gs[row, col])
          ax.axis('off')  # Turn off the axis for unused subplots


    fig.canvas.manager.set_window_title('Time Management')
    fig.set_size_inches(50, 25, forward=True) # w, h

    # plt.tight_layout()
    # fig.tight_layout()
    plt.savefig(self.save_path + "/three_week_summary.jpg")
    return

  def month_view(self, month):
    rows = 5
    cols = 7
    
    fig = plt.figure(figsize=(100, 100))
    plt.rcParams.update({'font.size': 22})
    gs = gridspec.GridSpec(rows, cols, figure=fig, hspace=0.1, top=0.9, bottom=0.1)
    # gs = gridspec.GridSpec(rows, cols, figure=fig)

    used_axes = set()
    fd = DT.date.today().replace(month=month, day=1)
    # get last day of month
    if month == 12:
      td = DT.date.today().replace(day=31)
    else:
      td = (DT.date.today().replace(month=month+1, day=1) - DT.timedelta(days=1))
    
    days = td.day
    for i, j in enumerate(range(fd.weekday(), days+1)):
      date = fd + DT.timedelta(days=i)

      # ax = self.line_chart(self.app, date, ax=plt.subplot(gs[int(i/cols), i%cols]))
      ax = self.pie_chart(self.app, date, ax=plt.subplot(gs[int(j/cols), j%cols]))
      
      weekday = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"]
      weekday = weekday[date.weekday()]
      
      if ax is not None:
        ax.set_title(weekday + " " + str(date))
        used_axes.add((int(i / cols), i % cols))
    
    for row in range(rows):
      for col in range(cols):
        if (row, col) not in used_axes:
          ax = plt.subplot(gs[row, col])
          ax.axis('off')  # Turn off the axis for unused subplots
    
    legend_handles = [mpatches.Patch(color=color, label=label) for label, color in self.colors.items()]
    fig.legend(handles=legend_handles, loc='center left', bbox_to_anchor=(0.92, 0.5))

    fig.canvas.manager.set_window_title('Time Management')
    fig.set_size_inches(50, 25, forward=True) # w, h
    # plt.tight_layout()
    # fig.tight_layout()
    print("Finished Month View " + str(month))
    plt.savefig(self.save_path + "/month_view_" + str(month) + ".jpg")
    return

  def summary(self, td):
    # Week summary
    fig = plt.figure(figsize=(100, 100))
    gs = gridspec.GridSpec(3, 3, figure=fig, hspace=0.1, top=0.9, bottom=0.1)

    # Line Chart
    ax = self.line_chart(self.app, td, ax=plt.subplot(gs[0,:]))
    ax.set_title("Today's recap")

    # Pie Charts
    self.pie_chart(self.app, td - DT.timedelta(days=2), plt.subplot(gs[1,0]))
    ax = self.pie_chart(self.app, td - DT.timedelta(days=1), plt.subplot(gs[1,1]))
    self.pie_chart(self.app, td, plt.subplot(gs[1,2]))
    ax.set_title("3day recap")

    # Bar Plot
    week_ago = td - DT.timedelta(days=7)
    ax = self.bar_chart(self.app, week_ago, td, ax=plt.subplot(gs[2,:]))
    ax.set_title("Week recap")

    fig.canvas.manager.set_window_title('Time Management')
    fig.set_size_inches(10, 9, forward=True) # w, h
    
    # plt.tight_layout()
    # fig.tight_layout()
    plt.savefig(self.save_path + "/summary.jpg")
    return
  
  def week_summary(self, td = DT.date.today()):
    # Week summary
    fig = plt.figure(figsize=(100, 100))
    gs = gridspec.GridSpec(7, 2, figure=fig)

    for i in range(7):
      # Line Chart
      date = td - DT.timedelta(days=7-i)
      ax = self.line_chart(self.app, date, ax=plt.subplot(gs[i, 0]))
      ax.sharex = ax
      ax.legend().set_visible(i == 0) 

      # Pie Charts
      ax = self.pie_chart(self.app, date, plt.subplot(gs[i, 1]))

    fig.canvas.manager.set_window_title('Time Management')
    fig.set_size_inches(10, 13, forward=True)
    plt.tight_layout()
    fig.tight_layout()
    plt.savefig(self.save_path + "/week_report.jpg")

  # Graph Creation
  # ==================================================================

  def line_chart(self, app, day, ax=None):
    day = pd.to_datetime(day)
    grouped_df = app[app["Effective_Day"]== day]

    unique_tags = grouped_df['Tag'].unique()

    # Create new DataFrame with 0 duration and End equal to minimum Start for each tag
    new_entries = pd.DataFrame({
        'Tag': unique_tags,
        'Duration': pd.to_timedelta(np.zeros(len(unique_tags)), unit='s'),
        'End': [grouped_df[grouped_df['Tag'] == tag]['Start'].min() for tag in unique_tags],
        'Start': [grouped_df[grouped_df['Tag'] == tag]['Start'].min() for tag in unique_tags]
    })
    grouped_df = pd.concat([grouped_df, new_entries], ignore_index=True)
    # grouped_df = grouped_df.append(new_entries, ignore_index=True)
    
    grouped_df = grouped_df.groupby(["Tag", "End"])["Duration"].sum().reset_index()
    grouped_df["Duration"] = grouped_df["Duration"].dt.total_seconds()/3600

    grouped_df.sort_values(by="End", inplace=True)
    grouped_df["Cumulative Duration"] = grouped_df.groupby("Tag")["Duration"].cumsum()
    
    pivot_df = grouped_df.pivot(index="End", columns="Tag", values="Cumulative Duration")
    pivot_df = pivot_df.ffill()

    if(not pivot_df.empty):
      ax = pivot_df.plot(kind="line", grid=True, color=self.colors, figsize=(10, 5), ax=ax)
      ax.xaxis.set_major_formatter(mdates.DateFormatter('%H'))
      ax.set_xlabel("")
      ax.set_ylim(bottom=0)

      ax.legend(loc='upper left')
      return ax
    else:
      return None

  def pie_chart(self, app, day, full_day = 15, ax=None):
    day = pd.to_datetime(day)
    grouped_df = app.groupby(["Effective_Day", "Tag"])["Duration"].sum().reset_index()
    grouped_df = grouped_df[grouped_df["Effective_Day"] == day]
    
    grouped_df["Duration"] = grouped_df["Duration"].dt.total_seconds()/3600
    grouped_df["Effective_Day"] = pd.to_datetime(grouped_df["Effective_Day"])

    grouped_df = grouped_df.set_index("Tag", drop=True)

    grouped_df.sort_values(by="Tag", inplace=True)
    
    if grouped_df["Duration"].sum() > 0:
      grouped_df.loc["None"] = {"Effective_Day": day , "Duration": full_day - grouped_df["Duration"].sum()}
    
    colors = [self.colors[tag] for tag in grouped_df.index]

    def label_func(pct, allvals):
      absolute = pct/100.*np.sum(allvals)
      return "{:.1f} h".format(absolute)
    
    if(not grouped_df.empty):
      ax = grouped_df.plot(
        kind="pie",
        y="Duration",
        legend=False,
        labels=None,
        title=day.strftime("%a %d.%m"), 
        ylabel="", 
        autopct=lambda pct: label_func(pct, grouped_df["Duration"],),
        ax=ax,
        colors=colors,
        startangle=90)
    
      return ax
    else:
      return None
  
  def bar_chart(self, app, start_date=None, end_date=None, ax=None):
    start_date = pd.to_datetime(start_date)
    end_date = pd.to_datetime(end_date)

    grouped_df = app.groupby(["Effective_Day", "Tag"])["Duration"].sum().reset_index()

    grouped_df["Effective_Day"] = pd.to_datetime(grouped_df["Effective_Day"])

    if(start_date is not None):
      grouped_df = grouped_df[grouped_df["Effective_Day"] > start_date]
    if(end_date is not None):
      grouped_df = grouped_df[grouped_df["Effective_Day"] <= end_date]
    
    # grouped_df["Effective_Day"] = grouped_df["Effective_Day"].dt.strftime("%a %d-%m")
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
  td = DT.date.today()# - DT.timedelta(days=3) # to date
  fd = td - DT.timedelta(days=24) # from date
  reloadTime = 600
  root_path = "/Users/matar/Documents/PugiosDocuments/OwnProjects/TimeManagement"
  save_path = root_path
  colors = {"Game": "blue", "Main":"green", "Side Project": "red", "Browser": "orange", "Journaling": "yellow", "Other":"grey", "Social":"purple", "None": "black"}
  export = True # False = Debug, True = Download application again
  month = td.month

  for arg in sys.argv:
    if arg.startswith("-sp"):
      save_path = arg.split("=")[1]
    elif arg.startswith("-debug"):
      export = False

  tm = TimeManagement(reloadTime, root_path, save_path, colors, export)
  

  # Create Report
  # =============
  print(f"Data from: {fd} - {td}")
  
  # 3 Week Report
  tm.import_and_preprocess(fd, td)
  tm.three_week_summary(td)

  # Month Report
  tm.month_import_and_preprocess(month)
  tm.month_view(month)

  # tm.summary(td)
  # tm.week_summary(td)
  # tm.continuous_day_chart()
  