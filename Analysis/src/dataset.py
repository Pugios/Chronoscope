#%%%%

import os
from os.path import exists
from os import system
import pandas as pd
import datetime as DT


class Dataset():

    def __init__(self, root_path, export=True):
        self.root_path = root_path
        self.export = export
        return

    def import_and_preprocess(self, fd=None, td=None):
        if self.export:
            self.export_app_data(fd, td)
        app, process_tags = self.load_files()
        process_tags = self.tagging(app, process_tags)
        app = self.merge_tags(app, process_tags)
        app = self.create_effective_day(app)
        self.app = app
        return

    def export_app_data(self, fd, td):
        app_path = os.path.join(self.root_path, "data", "applications.csv")
        if fd is None and td is None:
            system(
                '"/Program Files/ManicTime/mtc" export ManicTime/Applications ' + app_path)
        else:
            system('"/Program Files/ManicTime/mtc" export ManicTime/Applications ' +
                   app_path + " /fd:" + str(fd) + " /td:" + str(td))
        return

    def load_files(self):
        app_path = os.path.join(self.root_path, "data", "applications.csv")
        app = pd.read_csv(app_path, delimiter=",")
        
        app.Start = pd.to_datetime(app.Start)
        app.End = pd.to_datetime(app.End)
        app.Duration = pd.to_timedelta(app.Duration)

        process_tags_path = os.path.join(self.root_path, "data", "process_tags.csv")
        if exists(process_tags_path):
            process_tags = pd.read_csv(process_tags_path)
            
            # If file contains an extra unnamed index column, drop it
            unnamed = [
                c for c in process_tags.columns if c.startswith("Unnamed")]
            if unnamed:
                process_tags = process_tags.drop(columns=unnamed)
                
            # Try to detect which column contains Process names
            if "Process" not in process_tags.columns:
              raise Exception("Process Column not found in process_tags.csv!")
            
            # Ensure hierarchical columns exist
            if "Category" not in process_tags.columns:
                process_tags["Category"] = None
                
            if "Project" not in process_tags.columns:
                process_tags["Project"] = None
            # if old CSV had 'Tag' reuse as Label
            if "Label" not in process_tags.columns:
                if "Tag" in process_tags.columns:
                    process_tags["Label"] = process_tags["Tag"]
                else:
                    process_tags["Label"] = None
        else:
            process_tags = pd.DataFrame(columns=["Process", "Category", "Project", "Label"])
        return app, process_tags

    def month_import_and_preprocess(self, month):
      today = DT.date.today()
      fd = today.replace(month=month, day=1)
      td = (today.replace(month=month+1, day=1) - DT.timedelta(days=1))
      self.import_and_preprocess(fd, td)

    def is_date(self, date_string: str) -> bool:
        """Return True if date_string matches known date formats."""
        for fmt in ("%d.%m.%Y", "%Y-%m-%d"):
            try:
                DT.datetime.strptime(date_string, fmt)
                return True
            except ValueError:
                continue
        return False

# %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
# Debug

if __name__ == "__main__":
  root_path = "/Users/matar/Documents/PugiosDocuments/OwnProjects/TimeManagement"
  ds = Dataset(root_path, True)
  
  td = DT.date.today()
  fd = td - DT.timedelta(days=24)
  
  ds.export_app_data(fd, td)
  
  ds.load_files()

# %%
